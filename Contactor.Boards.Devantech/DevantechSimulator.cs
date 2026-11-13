using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Devantech;

/// <summary>
/// A fake Devantech ETH relay board of any of the four models, on a local TCP port, answering the binary commands as
/// the board does (<see cref="DevantechApi"/>): its module id (0x10), relays (0x20, 0x21, 0x23, 0x24), inputs (0x25, on
/// the models that have them), supply volts (0x78), serial number (0x77) and the TCP password (0x79, 0x7A, 0x7B). With
/// a password each connection starts locked, and locks again after 30 s on <see cref="TimeProvider"/> without a command;
/// a relay command while locked is refused (01). A command it doesn't know, or one the model doesn't have, gets no
/// answer at all, as on the board. It reads each TCP packet as the commands in it, and a password as the rest of its
/// packet. A pulse time switches the relay back by itself after the time on <see cref="TimeProvider"/>; a later command
/// for the relay ends its pulse, or starts it again. It never tells the app anything by itself. Several clients may
/// connect at once. Used by the tests and, in development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class DevantechSimulator : ISimulatedBoard
{
    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;
    private const byte SetOutputs = 0x23;
    private const byte GetSerialNumber = 0x77;
    private const byte GetVolts = 0x78;
    private const byte LogOut = 0x7B;

    private readonly ILogger _logger;
    private readonly DevantechLayout _layout;
    private readonly string? _password;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, string> _connections = new();
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly Lock _lock = new();
    private SimulatedPulses? _pulses;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ulong _relays;
    private ulong _inputs;
    private ulong? _lastRelaysSent;
    private ulong? _lastInputsSent;
    private int _requestCount;
    private int _unansweredCount;

    public DevantechSimulator(BoardModel model)
        : this(model, null, NullLogger.Instance)
    {
    }

    /// <param name="model">One of <see cref="DevantechModels.All"/>.</param>
    /// <param name="password">The TCP password, or null or empty for none.</param>
    /// <param name="logger">Where it logs; never the password.</param>
    public DevantechSimulator(BoardModel model, string? password, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        if (!DevantechModels.IsDevantech(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Devantech board.", nameof(model));
        }

        if (!string.IsNullOrEmpty(password) && DevantechModels.CheckPassword(password) is { } error)
        {
            throw new ArgumentException(error, nameof(password));
        }

        Model = model;
        _layout = DevantechModels.Layout(model);
        _password = string.IsNullOrEmpty(password) ? null : password;
        _logger = logger;
        ModuleId = _layout.ModuleId;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model { get; }

    /// <summary>The clock for the traffic's times, the 30 s lock and the end of a pulse.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The relays whose pulse is still running.</summary>
    public ChannelMask Pulsing => new(Pulses.Running);

    // Made on first use, so it takes the TimeProvider set by the initializer.
    private SimulatedPulses Pulses
    {
        get
        {
            if (Volatile.Read(ref _pulses) is { } pulses)
            {
                return pulses;
            }

            var created = new SimulatedPulses(TimeProvider);
            return Interlocked.CompareExchange(ref _pulses, created, null) ?? created;
        }
    }

    public int Port { get; private set; }

    /// <summary>Whether it has a TCP password.</summary>
    public bool HasLogin => _password is not null;

    /// <summary>Its relays and inputs; the clients are the open connections.</summary>
    public SimulatedBoardState State
    {
        get
        {
            lock (_lock)
            {
                return new SimulatedBoardState(
                    new ChannelMask(_relays),
                    new ChannelMask(_inputs),
                    [.. _connections.Values.Order(StringComparer.Ordinal)]);
            }
        }
    }

    public ulong Relays
    {
        get
        {
            lock (_lock)
            {
                return _relays;
            }
        }
    }

    /// <summary>What it answers first to 0x10: its model's id, unless a test makes it another model.</summary>
    public byte ModuleId { get; set; }

    /// <summary>Commands answered since start.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Commands left without an answer: unknown, not on this model, or <see cref="NoAnswer"/>.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>How many connections are open.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>When true, commands are read but never answered, as if the board hung.</summary>
    public bool NoAnswer { get; set; }

    /// <summary>Relays (bit 0 = relay 1) whose 0x20 and 0x21 are answered 01 and not carried out, for the tests.</summary>
    public ulong FailRelays { get; set; }

    /// <summary>
    /// The commands received, as hex, e.g. "20 03 00"; a password as <see cref="DevantechTraceText.HiddenPassword"/>.
    /// The newest 1,000.
    /// </summary>
    public IReadOnlyList<string> ReceivedRequests => [.. _receivedRequests];

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

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        var bit = Model.Capabilities.Relay(relay).Bits;
        Pulses.Cancel(bit);
        Change(() => _relays = turnOn ? _relays | bit : _relays & ~bit);
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Model.Capabilities.InputCount);
        var bit = 1UL << (input - 1);
        Change(() => _inputs = closed ? _inputs | bit : _inputs & ~bit);
        return Task.CompletedTask;
    }

    public void DropConnections()
    {
        foreach (var connection in _connections.Keys)
        {
            connection.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Pulses.Dispose();
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        DropConnections();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _listener?.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        List<Task> handlers = [];
        try
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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
            // Stopped before the loop got to accept.
        }

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        if (RejectConnections)
        {
            tcp.Dispose();
            return;
        }

        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _connections.TryAdd(tcp, remote);
        RaiseChanged();
        Trace(BoardTraceDirection.None, $"The app connected from {remote}", null);
        var session = new Session();
        try
        {
            var stream = tcp.GetStream();
            var packet = new byte[256];
            while (true)
            {
                var read = await stream.ReadAsync(packet, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                foreach (var answer in HandlePacket(packet.AsSpan(0, read), session))
                {
                    await stream.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the simulator is stopping.
        }
        finally
        {
            _connections.TryRemove(tcp, out _);
            tcp.Dispose();
            RaiseChanged();
        }
    }

    // The commands in one packet, one after the other, and their answers. A command cut short or unknown ends the
    // packet without an answer.
    private List<byte[]> HandlePacket(ReadOnlySpan<byte> packet, Session session)
    {
        List<byte[]> answers = [];
        while (!packet.IsEmpty)
        {
            var length = CommandLength(packet);
            if (length is null || length > packet.Length)
            {
                // Only the command byte: the rest of the packet may hold a password.
                var shown = packet.Length > 1 ? $"{DevantechProtocol.Hex(packet[..1])} …" : DevantechProtocol.Hex(packet);
                Remember(shown);
                Trace(BoardTraceDirection.Sent, DevantechTraceText.Request(packet[..1]), shown);
                Silent(length is null ? "No answer: a command this board doesn't know" : "No answer: the command was cut short");
                break;
            }

            var command = packet[..length.Value];
            packet = packet[length.Value..];
            var bytes = DevantechTraceText.RequestBytes(command);
            Remember(bytes);
            Trace(BoardTraceDirection.Sent, DevantechTraceText.Request(command), bytes, isRoutine: command[0] is DevantechApi.GetOutputs or DevantechApi.GetInputs);
            if (NoAnswer)
            {
                Silent("No answer: the board hangs");
                continue;
            }

            if (Handle(command, session) is { } answer)
            {
                Interlocked.Increment(ref _requestCount);
                answers.Add(answer);
            }
        }

        return answers;
    }

    // How long the command at the start of the packet is, or null for one the model doesn't have.
    private int? CommandLength(ReadOnlySpan<byte> packet)
    {
        return packet[0] switch
        {
            DevantechApi.GetModuleInfo or DevantechApi.GetOutputs or GetSerialNumber or GetVolts or DevantechApi.GetUnlockTime or LogOut => 1,
            DevantechApi.GetInputs when _layout.InputBytes > 0 => 1,
            DevantechApi.DigitalActive or DevantechApi.DigitalInactive => 3,
            SetOutputs => 1 + _layout.OutputBytes,

            // No length and no end: the password is the rest of the packet.
            DevantechApi.Password => packet.Length,
            _ => null,
        };
    }

    // The answer, or null to send none.
    private byte[]? Handle(ReadOnlySpan<byte> command, Session session)
    {
        var now = TimeProvider.GetUtcNow();
        var locked = _password is not null && !(session.UnlockedUntil > now);

        // Every command keeps an unlocked board unlocked for another 30 s. Which commands count is to check on a real
        // board.
        if (!locked && _password is not null)
        {
            session.UnlockedUntil = now.AddSeconds(DevantechApi.UnlockSeconds);
        }

        switch (command[0])
        {
            case DevantechApi.GetModuleInfo:
                byte[] info = [ModuleId, 2, 28];
                return Answer(info, DevantechTraceText.Module(info));
            case DevantechApi.GetOutputs:
                return ReadBits(output: true);
            case DevantechApi.GetInputs:
                return ReadBits(output: false);
            case DevantechApi.DigitalActive or DevantechApi.DigitalInactive:
                return Switch(command, locked);
            case SetOutputs:
                return SetAll(command[1..], locked);
            case GetSerialNumber:
                return Answer([0x00, 0x04, 0xA3, 0x12, 0x34, 0x56], "Serial number");
            case GetVolts:
                return Answer([120], "12.0 V");
            case DevantechApi.GetUnlockTime:
                var left = _password is null
                    ? DevantechApi.NoPassword
                    : locked ? DevantechApi.Locked : (byte)Math.Ceiling((session.UnlockedUntil!.Value - now).TotalSeconds);
                return Answer([left], DevantechTraceText.UnlockTime(left));
            case DevantechApi.Password:
                return CheckPassword(command[1..], session, now);
            case LogOut:
                session.UnlockedUntil = null;
                return Answer([DevantechApi.Success], "Locked");
            default:
                return Silent("No answer: a command this board doesn't know");
        }
    }

    private byte[] ReadBits(bool output)
    {
        ulong bits;
        bool routine;
        lock (_lock)
        {
            bits = output ? _relays : _inputs;
            routine = (output ? _lastRelaysSent : _lastInputsSent) == bits;
            if (output)
            {
                _lastRelaysSent = bits;
            }
            else
            {
                _lastInputsSent = bits;
            }
        }

        var capabilities = Model.Capabilities;
        return output
            ? Answer(DevantechProtocol.OutputBytes(bits, _layout.OutputBytes), DevantechTraceText.Relays(bits, capabilities.RelayCount), routine)
            : Answer(DevantechProtocol.InputBytes(bits, _layout.InputBytes), DevantechTraceText.Inputs(bits, capabilities.InputCount), routine);
    }

    // 0x20 or 0x21: a relay out of range, one in FailRelays, or any while locked is refused (01). With a pulse time the
    // relay switches back after it, as on the board; a command for the relay ends a pulse that runs.
    private byte[] Switch(ReadOnlySpan<byte> command, bool locked)
    {
        var relay = command[1];
        var on = command[0] == DevantechApi.DigitalActive;
        var pulse = command[2];
        var bit = relay is >= 1 and <= 64 ? 1UL << (relay - 1) : 0;
        if (locked || relay < 1 || relay > Model.Capabilities.RelayCount || (FailRelays & bit) != 0)
        {
            return Answer([DevantechApi.Failure], DevantechTraceText.Switched(command, DevantechApi.Failure), isError: true);
        }

        Pulses.Cancel(bit);
        Change(() => _relays = on ? _relays | bit : _relays & ~bit);
        if (pulse != DevantechApi.Permanent)
        {
            Pulses.Start(bit, DevantechApi.PulseUnit * pulse, relays => Change(() => _relays = on ? _relays & ~relays : _relays | relays));
        }

        return Answer([DevantechApi.Success], DevantechTraceText.Switched(command, DevantechApi.Success));
    }

    // 0x23: every relay at once, low byte first; bits past the relays are dropped.
    private byte[] SetAll(ReadOnlySpan<byte> image, bool locked)
    {
        if (locked)
        {
            return Answer([DevantechApi.Failure], "Refused: locked", isError: true);
        }

        var relays = DevantechProtocol.Outputs(image, Model.Capabilities.RelayCount);
        Pulses.Cancel(Model.Capabilities.AllRelays.Bits);
        Change(() => _relays = relays);
        return Answer([DevantechApi.Success], DevantechTraceText.Relays(relays, Model.Capabilities.RelayCount));
    }

    private byte[] CheckPassword(ReadOnlySpan<byte> password, Session session, DateTimeOffset now)
    {
        var right = _password is null || password.SequenceEqual(Encoding.ASCII.GetBytes(_password));
        if (right)
        {
            session.UnlockedUntil = now.AddSeconds(DevantechApi.UnlockSeconds);
        }

        var answer = right ? DevantechApi.PasswordAccepted : DevantechApi.PasswordRefused;
        return Answer([answer], DevantechTraceText.PasswordAnswer(answer), isError: !right);
    }

    private byte[] Answer(byte[] answer, string text, bool routine = false, bool isError = false)
    {
        Trace(BoardTraceDirection.Received, text, DevantechProtocol.Hex(answer), isError, routine);
        return answer;
    }

    private byte[]? Silent(string text)
    {
        Interlocked.Increment(ref _unansweredCount);
        Trace(BoardTraceDirection.None, text, null, isError: true);
        return null;
    }

    private void Remember(string request)
    {
        _receivedRequests.Enqueue(request);
        while (_receivedRequests.Count > ReceivedRequestsKept && _receivedRequests.TryDequeue(out _))
        {
        }
    }

    private void Change(Action change)
    {
        bool changed;
        lock (_lock)
        {
            var before = (_relays, _inputs);
            change();
            changed = before != (_relays, _inputs);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes, bool isError = false, bool isRoutine = false)
    {
        if (Traced is not { } handlers)
        {
            return;
        }

        var entry = new BoardTraceEntry(TimeProvider.GetUtcNow(), direction, text, bytes, isError, isRoutine);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardTraceEntry>>())
        {
            try
            {
                handler(this, entry);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated board.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated board.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    // One connection's lock: the password unlocks a connection, not the board.
    private sealed class Session
    {
        public DateTimeOffset? UnlockedUntil { get; set; }
    }
}
