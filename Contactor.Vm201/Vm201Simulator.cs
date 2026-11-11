using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Vm201;

/// <summary>
/// A fake VM201 on a local TCP port, implementing the whole protocol: optional login, channel names, status pushed
/// on every change and no reply to a command that changes nothing (as the real board). Used by the tests and, in
/// development, by Contactor.Simulator (started by the Aspire AppHost). It listens on the loopback interface only.
/// </summary>
public sealed class Vm201Simulator : IAsyncDisposable
{
    private const int NameLength = Vm201Command.NameDataLength - 1;
    private const int InputChannel = 9;

    private readonly string? _username;
    private readonly string? _password;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly ConcurrentQueue<Vm201Packet> _receivedCommands = new();

    // One gate for the board state and for every write, so status packets always go out in the order of the changes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private byte _outputs;
    private byte _timerMask;
    private bool _input;
    private int _connectionCount;
    private int _deniedLoginCount;

    /// <summary>A board without login.</summary>
    public Vm201Simulator()
        : this(null, null, NullLogger.Instance)
    {
    }

    /// <summary>A board that requires <paramref name="username"/> and <paramref name="password"/>, or no login if both are null.</summary>
    public Vm201Simulator(string? username, string? password, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if ((username is null) != (password is null))
        {
            throw new ArgumentException("Give both a user name and a password, or neither.", nameof(password));
        }

        _username = username;
        _password = password;
        _logger = logger;
    }

    /// <summary>The port it listens on, after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    public byte Outputs => Volatile.Read(ref _outputs);

    public byte TimerMask => Volatile.Read(ref _timerMask);

    public bool Input => Volatile.Read(ref _input);

    /// <summary>Connections accepted since start.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>Logins rejected because of a wrong user name or password.</summary>
    public int DeniedLoginCount => Volatile.Read(ref _deniedLoginCount);

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>
    /// When true, the board stops reading and sending without closing the socket, as on a half-open connection.
    /// </summary>
    public bool Unresponsive { get; set; }

    /// <summary>Every packet received from logged-in clients, in order.</summary>
    public IReadOnlyList<Vm201Packet> ReceivedCommands => [.. _receivedCommands];

    /// <summary>Starts listening on 127.0.0.1. Port 0 picks a free port.</summary>
    public void Start(int port = 0)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The simulator is already started.");
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_listener, _stopping.Token);
        _logger.SimulatorListening(Port);
    }

    /// <summary>Changes the outputs as the board's web page, buttons or timers would, and pushes the status.</summary>
    public Task SetOutputsAsync(byte outputs)
    {
        return ChangeAsync(() => _outputs = outputs);
    }

    public Task SetTimerMaskAsync(byte timerMask)
    {
        return ChangeAsync(() => _timerMask = timerMask);
    }

    public Task SetInputAsync(bool input)
    {
        return ChangeAsync(() => _input = input);
    }

    /// <summary>Closes every open connection, as if the network dropped.</summary>
    public void DropConnections()
    {
        foreach (var connection in _connections.Keys)
        {
            connection.Tcp.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        DropConnections();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _listener?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        List<Task> handlers = [];
        try
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _connectionCount);
                handlers.RemoveAll(h => h.IsCompleted);
                handlers.Add(HandleConnectionAsync(tcp, cancellationToken));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped before the loop got to accept: the listener says it isn't listening.
        }

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var connection = new Connection(tcp);
        _connections.TryAdd(connection, 0);
        try
        {
            if (RejectConnections)
            {
                return;
            }

            var reader = new Vm201PacketReader(connection.Stream, new Vm201PacketDecoder(_logger));
            while (Unresponsive)
            {
                // Accepts the connection but never answers, until the client gives up.
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await LoginAsync(connection, reader, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SendAsync(connection, Vm201PacketCodec.Encode(Vm201Command.LoggedIn), cancellationToken).ConfigureAwait(false);
                for (var channel = 1; channel <= InputChannel; channel++)
                {
                    await SendAsync(connection, EncodeName(channel), cancellationToken).ConfigureAwait(false);
                }

                await SendAsync(connection, EncodeStatus(), cancellationToken).ConfigureAwait(false);
                connection.LoggedIn = true;
            }
            finally
            {
                _gate.Release();
            }

            while (true)
            {
                var packet = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (Unresponsive)
                {
                    continue;
                }

                _receivedCommands.Enqueue(packet);
                if (packet.Command == Vm201Command.Closed)
                {
                    return;
                }

                await HandleCommandAsync(connection, packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the simulator is stopping.
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            tcp.Dispose();
        }
    }

    private async Task<bool> LoginAsync(Connection connection, Vm201PacketReader reader, CancellationToken cancellationToken)
    {
        if (_username is null)
        {
            return true;
        }

        await SendLockedAsync(connection, Vm201PacketCodec.Encode(Vm201Command.Auth), cancellationToken).ConfigureAwait(false);
        string? username = null;
        string? password = null;
        while (username is null || password is null)
        {
            var packet = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (packet.Command)
            {
                case Vm201Command.Username:
                    username = Vm201PacketCodec.DecodeText(packet.Data.Span);
                    break;
                case Vm201Command.Password:
                    password = Vm201PacketCodec.DecodeText(packet.Data.Span);
                    break;
                case Vm201Command.Closed:
                    return false;
                default:
                    break;
            }
        }

        if (username == _username && password == _password)
        {
            return true;
        }

        Interlocked.Increment(ref _deniedLoginCount);
        await SendLockedAsync(connection, Vm201PacketCodec.Encode(Vm201Command.AccessDenied), cancellationToken).ConfigureAwait(false);
        await SendLockedAsync(connection, Vm201PacketCodec.Encode(Vm201Command.Closed), cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task HandleCommandAsync(Connection connection, Vm201Packet packet, CancellationToken cancellationToken)
    {
        if (packet.Command == Vm201Command.StatusRequest)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SendAsync(connection, EncodeStatus(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            return;
        }

        if (packet.Data.Length != 1)
        {
            _logger.IgnoredPacket((char)packet.Command);
            return;
        }

        var mask = packet.Data.Span[0];
        Action? change = packet.Command switch
        {
            Vm201Command.On => () => _outputs |= mask,
            Vm201Command.Off => () => _outputs &= (byte)~mask,
            Vm201Command.Toggle => () => _outputs ^= mask,
            Vm201Command.Update => () => _outputs = mask,
            Vm201Command.TimerEnable => () => _timerMask |= mask,
            Vm201Command.TimerDisable => () => _timerMask &= (byte)~mask,
            Vm201Command.TimerToggle => () => _timerMask ^= mask,
            _ => null,
        };

        if (change is null)
        {
            _logger.IgnoredPacket((char)packet.Command);
            return;
        }

        await ChangeAsync(change).ConfigureAwait(false);
    }

    /// <summary>Applies a change and pushes the status to every logged-in client, but only if something changed.</summary>
    private async Task ChangeAsync(Action change)
    {
        await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            var before = EncodeStatus();
            change();
            var after = EncodeStatus();
            if (before.AsSpan().SequenceEqual(after))
            {
                return;
            }

            foreach (var connection in _connections.Keys)
            {
                if (!connection.LoggedIn || Unresponsive)
                {
                    continue;
                }

                try
                {
                    await SendAsync(connection, after, _stopping.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // That client is gone; its handler cleans up.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendLockedAsync(Connection connection, byte[] packet, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync(connection, packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Callers hold _gate.
    private static async Task SendAsync(Connection connection, byte[] packet, CancellationToken cancellationToken)
    {
        await connection.Stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private byte[] EncodeStatus()
    {
        return Vm201PacketCodec.Encode(Vm201Command.Status, [_outputs, _timerMask, (byte)(_input ? 1 : 0)]);
    }

    private static byte[] EncodeName(int channel)
    {
        var name = channel == InputChannel ? "Input" : $"Relay {channel}";
        var data = new byte[Vm201Command.NameDataLength];
        data[0] = (byte)channel;
        Encoding.ASCII.GetBytes(name, data.AsSpan(1, NameLength));
        return Vm201PacketCodec.Encode(Vm201Command.Name, data);
    }

    private sealed class Connection
    {
        public Connection(TcpClient tcp)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
        }

        public TcpClient Tcp { get; }

        public NetworkStream Stream { get; }

        public volatile bool LoggedIn;
    }
}
