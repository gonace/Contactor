using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.SmartDen;

public static class SmartDenApi
{
    public const int DefaultPort = 502;
    public const byte DefaultUnitId = 0;
    public const byte ReadCoils = 0x01;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte ExceptionFlag = 0x80;
    public const int RelayCount = 16;
    public static readonly TimeSpan PulseUnit = TimeSpan.FromMilliseconds(100);
}

public static class SmartDenModels
{
    public const string Brand = "Denkovi";
    public static PulseAbility Pulse { get; } = new(
        SmartDenApi.PulseUnit,
        TimeSpan.FromTicks(SmartDenApi.PulseUnit.Ticks * ushort.MaxValue),
        SmartDenApi.PulseUnit);
    public static BoardModel Ip16RMt { get; } = new(
        "denkovi-smartden-ip-16r-mt", Brand, "smartDEN IP-16R-MT",
        new BoardCapabilities(16, 0, ReportsChanges: false, Pulse: Pulse),
        SmartDenApi.DefaultPort, LoginMaxLength: 0);
    public static IReadOnlyList<BoardModel> All { get; } = [Ip16RMt];
    public static bool IsSmartDen(BoardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return All.Contains(model);
    }
}

public sealed class SmartDenOptions
{
    public const string SectionName = "SmartDen";
    public string Host { get; set; } = "";
    [Range(1, 65535)]
    public int Port { get; set; } = SmartDenApi.DefaultPort;

    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;

    [Range(0, 255)]
    public int UnitId { get; set; } = SmartDenApi.DefaultUnitId;
}

public sealed record SmartDenRequest(byte[] Frame, ushort TransactionId, byte UnitId, byte Function, ushort Address, ushort Value);

public sealed record SmartDenStatus(ulong Relays);

/// <summary>The Modbus TCP subset used by smartDEN. MBAP framing is used; there is no RTU CRC.</summary>
public static class SmartDenModbus
{
    public static SmartDenRequest ReadCoils(ushort transaction, byte unit = SmartDenApi.DefaultUnitId) =>
        Create(transaction, unit, SmartDenApi.ReadCoils, 0, SmartDenApi.RelayCount);

    public static SmartDenRequest WriteCoil(ushort transaction, int relay, bool on, byte unit = SmartDenApi.DefaultUnitId)
    {
        CheckRelay(relay);
        return Create(transaction, unit, SmartDenApi.WriteSingleCoil, (ushort)(relay - 1), on ? (ushort)0xFF00 : (ushort)0);
    }

    public static SmartDenRequest Pulse(ushort transaction, int relay, TimeSpan length, byte unit = SmartDenApi.DefaultUnitId)
    {
        CheckRelay(relay);
        if (!SmartDenModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The smartDEN pulse range is 0.1 to 6553.5 seconds in 0.1-second steps.");
        }

        var value = checked((ushort)(length.Ticks / SmartDenApi.PulseUnit.Ticks));
        return Create(transaction, unit, SmartDenApi.WriteSingleRegister, (ushort)(relay - 1), value);
    }

    public static async Task<byte[]> ReadResponseAsync(Stream stream, SmartDenRequest request, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var transaction = BinaryPrimitives.ReadUInt16BigEndian(header);
        var protocol = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (transaction != request.TransactionId || protocol != 0 || length is < 2 or > 254 || header[6] != request.UnitId)
        {
            throw new InvalidDataException("The smartDEN returned an invalid Modbus TCP header.");
        }

        var pdu = new byte[length - 1];
        await stream.ReadExactlyAsync(pdu, cancellationToken).ConfigureAwait(false);
        if (pdu.Length < 2)
        {
            throw new InvalidDataException("The smartDEN returned an incomplete Modbus response.");
        }

        if (pdu[0] == (byte)(request.Function | SmartDenApi.ExceptionFlag))
        {
            throw new BoardRefusedException($"The smartDEN refused the request (Modbus exception {pdu[1]}).");
        }

        if (pdu[0] != request.Function)
        {
            throw new InvalidDataException($"The smartDEN answered function 0x{pdu[0]:X2}, not 0x{request.Function:X2}.");
        }

        if (request.Function == SmartDenApi.ReadCoils)
        {
            if (pdu.Length != 4 || pdu[1] != 2)
            {
                throw new InvalidDataException("The smartDEN returned an invalid relay-state response.");
            }
        }
        else if (pdu.Length != 5
            || BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)) != request.Address
            || BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3)) != request.Value)
        {
            throw new InvalidDataException("The smartDEN did not echo the requested relay command.");
        }

        return pdu;
    }

    public static ulong RelayBits(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 4 || pdu[0] != SmartDenApi.ReadCoils || pdu[1] != 2)
        {
            throw new InvalidDataException("A smartDEN relay response must contain exactly two data bytes.");
        }

        return (ulong)(pdu[2] | (pdu[3] << 8));
    }

    public static byte[] Response(ushort transaction, byte unit, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), checked((ushort)(pdu.Length + 1)));
        frame[6] = unit;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public static string Hex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(part => new string(part)));

    private static SmartDenRequest Create(ushort transaction, byte unit, byte function, ushort address, ushort value)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        frame[6] = unit;
        frame[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), value);
        return new SmartDenRequest(frame, transaction, unit, function, address, value);
    }

    private static void CheckRelay(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, SmartDenApi.RelayCount);
    }
}

/// <summary>Polls one smartDEN board over its single supported TCP socket.</summary>
public sealed class SmartDenClient : IPolledBoard<SmartDenStatus>, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly BoardModel _model;
    private readonly SmartDenOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SmartDenClient> _logger;
    private readonly BoardPoller<SmartDenStatus> _poller;
    private BoardEndpoint _endpoint;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _transaction;

    public SmartDenClient(IOptions<SmartDenOptions> options, BoardModel model, TimeProvider time, ILogger<SmartDenClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (!SmartDenModels.IsSmartDen(model))
        {
            throw new ArgumentException($"{model.Name} isn't a smartDEN model.", nameof(model));
        }

        var value = options.Value;
        if (value.Port is < 1 or > 65535 || value.PollSeconds is < 1 or > 60 || value.UnitId is < 0 or > 255)
        {
            throw new ArgumentException("SmartDen settings are outside their valid ranges.", nameof(options));
        }

        _model = model;
        _options = value;
        _time = time;
        _logger = logger;
        _endpoint = new BoardEndpoint(value.Host, value.Port);
        _poller = new BoardPoller<SmartDenStatus>(this, time) { PollInterval = TimeSpan.FromSeconds(value.PollSeconds) };
    }

    public event EventHandler<BoardState>? StateChanged;
    public event EventHandler<BoardTraceEntry>? Traced;
    public BoardModel Model => _model;
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    public BoardState State
    {
        get
        {
            var state = _poller.State;
            return new BoardState(state.IsConnected, new ChannelMask(state.Reading?.Relays ?? 0), ChannelMask.None, state.LastContactUtc);
        }
    }

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        _poller.Forget();
        _poller.Restart();
    }

    public Task RunAsync(CancellationToken cancellationToken) => _poller.RunAsync(cancellationToken);

    public Task<ulong> TurnOnAsync(ulong mask, CancellationToken cancellationToken = default) =>
        CommandAsync(mask, relay => SmartDenModbus.WriteCoil(NextTransaction(), relay, true, (byte)_options.UnitId), cancellationToken);

    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default) =>
        CommandAsync(mask, relay => SmartDenModbus.WriteCoil(NextTransaction(), relay, false, (byte)_options.UnitId), cancellationToken);

    public Task<ulong> PulseAsync(ulong mask, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!SmartDenModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The smartDEN pulse range is 0.1 to 6553.5 seconds in 0.1-second steps.");
        }

        return CommandAsync(mask, relay => SmartDenModbus.Pulse(NextTransaction(), relay, length, (byte)_options.UnitId), cancellationToken);
    }

    public void Refresh()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        _poller.PollNow();
    }

    public void Dispose()
    {
        Close();
        _poller.Dispose();
    }

    async Task IPolledBoard<SmartDenStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Endpoint.HasHost)
        {
            throw new InvalidOperationException("No smartDEN address has been set.");
        }

        Close();
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, timeout.Token).ConfigureAwait(false);
            _tcp = tcp;
            _stream = tcp.GetStream();
            Trace(BoardTraceDirection.None, $"Connected to {Endpoint}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    Task<SmartDenStatus> IPolledBoard<SmartDenStatus>.ReadAsync(CancellationToken cancellationToken) =>
        ReadStatusAsync(cancellationToken);

    void IPolledBoard<SmartDenStatus>.OnStateChanged(PolledState<SmartDenStatus> state)
    {
        var boardState = new BoardState(state.IsConnected, new ChannelMask(state.Reading?.Relays ?? 0), ChannelMask.None, state.LastContactUtc);
        if (StateChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try
            {
                handler(this, boardState);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "A smartDEN state subscriber failed.");
            }
        }
    }

    void IPolledBoard<SmartDenStatus>.OnStep(BoardPollStep kind, string? detail)
    {
        switch (kind)
        {
            case BoardPollStep.Connecting:
                Trace(BoardTraceDirection.None, $"Connecting to {Endpoint}");
                break;
            case BoardPollStep.Lost:
                Close();
                Trace(BoardTraceDirection.None, $"Connection lost: {detail}", isError: true);
                break;
            case BoardPollStep.Waiting:
                Trace(BoardTraceDirection.None, $"Trying again in {detail} s");
                break;
            case BoardPollStep.Stopped:
                Close();
                Trace(BoardTraceDirection.None, "The app closed the connection");
                break;
        }
    }

    private async Task<ulong> CommandAsync(ulong mask, Func<int, SmartDenRequest> makeRequest, CancellationToken cancellationToken)
    {
        if (!_model.Capabilities.AllRelays.ContainsAll(new ChannelMask(mask)))
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, "The smartDEN has relays 1–16 only.");
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        if (mask == 0)
        {
            return State.Relays.Bits;
        }

        var taken = new StrongBox<ulong>();
        try
        {
            await _poller.RunCommandAsync(async token =>
            {
                string? refusal = null;
                foreach (var relay in new ChannelMask(mask).Channels)
                {
                    try
                    {
                        var request = makeRequest(relay);
                        var answer = await ExchangeAsync(request, false, token).ConfigureAwait(false);
                        Trace(BoardTraceDirection.Received, $"Relay {relay} command confirmed", SmartDenModbus.Hex(answer));
                        taken.Value |= 1UL << (relay - 1);
                    }
                    catch (BoardRefusedException ex)
                    {
                        refusal ??= ex.Message;
                        Trace(BoardTraceDirection.None, $"The board refused relay {relay}: {ex.Message}", isError: true);
                    }
                }

                if (taken.Value == 0)
                {
                    throw new BoardRefusedException(refusal ?? "The smartDEN didn't take the command.");
                }

                return await ReadStatusAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (BoardRefusedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidDataException)
        {
            throw new BoardNotConnectedException("The smartDEN didn't answer or returned an unreadable response; the command may not have been carried out.", ex);
        }
    }

    private async Task<SmartDenStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var request = SmartDenModbus.ReadCoils(NextTransaction(), (byte)_options.UnitId);
        var answer = await ExchangeAsync(request, true, cancellationToken).ConfigureAwait(false);
        var bits = SmartDenModbus.RelayBits(answer);
        Trace(BoardTraceDirection.Received, bits == 0 ? "All relays off" : $"Relays on: {new ChannelMask(bits)}", SmartDenModbus.Hex(answer), isRoutine: true);
        return new SmartDenStatus(bits);
    }

    private async Task<byte[]> ExchangeAsync(SmartDenRequest request, bool routine, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("The smartDEN connection is closed.");
        try
        {
            Trace(BoardTraceDirection.Sent, DescribeRequest(request), SmartDenModbus.Hex(request.Frame), isRoutine: routine);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            await stream.WriteAsync(request.Frame, timeout.Token).ConfigureAwait(false);
            return await SmartDenModbus.ReadResponseAsync(stream, request, timeout.Token).ConfigureAwait(false);
        }
        catch (BoardRefusedException)
        {
            // A Modbus exception is a complete, valid response; keep the single socket open for the next relay.
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Close();
            throw new TimeoutException("The smartDEN did not answer within five seconds.", ex);
        }
        catch
        {
            // A timeout or malformed response may leave the stream out of step. Reconnect before another request.
            Close();
            throw;
        }
    }

    private ushort NextTransaction() => unchecked((ushort)Interlocked.Increment(ref _transaction));

    private void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false)
    {
        if (Traced is not { } handlers)
        {
            return;
        }

        var entry = new BoardTraceEntry(_time.GetUtcNow(), direction, text, bytes, isError, isRoutine);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardTraceEntry>>())
        {
            try { handler(this, entry); }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "A smartDEN traffic subscriber failed.");
            }
        }
    }

    private static string DescribeRequest(SmartDenRequest request) => request.Function switch
    {
        SmartDenApi.ReadCoils => "Read relay states",
        SmartDenApi.WriteSingleCoil => $"Turn relay {request.Address + 1} {(request.Value == 0xFF00 ? "on" : "off")}",
        SmartDenApi.WriteSingleRegister => $"Pulse relay {request.Address + 1}",
        _ => $"Modbus function 0x{request.Function:X2}",
    };
}

/// <summary>The smartDEN IP-16R-MT as the app's relay-board interface.</summary>
public sealed class SmartDenBoard : IRelayBoard
{
    private readonly SmartDenClient _client;
    private readonly ILogger<SmartDenBoard> _logger;

    public SmartDenBoard(SmartDenClient client, ILogger<SmartDenBoard> logger)
    {
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;
    public BoardCapabilities Capabilities => _client.Model.Capabilities;
    public BoardState State => _client.State;
    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default) =>
        new(await _client.TurnOnAsync(relays.Bits, cancellationToken).ConfigureAwait(false));
    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default) =>
        new(await _client.TurnOffAsync(relays.Bits, cancellationToken).ConfigureAwait(false));
    public async Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default) =>
        new(await _client.PulseAsync(relays.Bits, length, cancellationToken).ConfigureAwait(false));
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, BoardState state)
    {
        if (StateChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try { handler(this, state); }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "A smartDEN board-state subscriber failed.");
            }
        }
    }
}

public sealed class SmartDenConnection : IBoardConnection
{
    private readonly SmartDenClient _client;
    private readonly SmartDenOptions _options;

    public SmartDenConnection(SmartDenClient client, IOptions<SmartDenOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);
    public BoardLogin DefaultLogin => BoardLogin.None;
    public BoardEndpoint Endpoint => _client.Endpoint;
    public BoardLogin Login => BoardLogin.None;
    public void SetEndpoint(BoardEndpoint endpoint) => _client.SetEndpoint(endpoint);

    public void SetLogin(BoardLogin login)
    {
        if (CheckLogin(login) is { } problem) throw new ArgumentException(problem.ToString(), nameof(login));
    }

    public LoginProblem? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.IsNone ? null : new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: SmartDenModels.Ip16RMt.Name);
    }

    public Task RunAsync(CancellationToken cancellationToken) => _client.RunAsync(cancellationToken);
}

public sealed class SmartDenTraffic : IBoardTraffic, IDisposable
{
    private readonly SmartDenClient _client;
    public SmartDenTraffic(SmartDenClient client)
    {
        _client = client;
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;
    public void Dispose() => _client.Traced -= OnTraced;
    private void OnTraced(object? sender, BoardTraceEntry entry) => Traced?.Invoke(this, entry);
}

/// <summary>Simulator for the smartDEN IP-16R-MT. It serves one Modbus TCP client at a time.</summary>
public sealed class SmartDenSimulator : ISimulatedBoard
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private readonly SimulatedPulses _pulses;
    private readonly ILogger _logger;
    private TcpListener? _listener;
    private TcpClient? _connection;
    private Task? _acceptLoop;
    private ulong _relays;
    private int _unanswered;

    public SmartDenSimulator() : this(TimeProvider.System, NullLogger.Instance) { }
    public SmartDenSimulator(TimeProvider timeProvider, ILogger? logger = null)
    {
        TimeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
        _pulses = new SimulatedPulses(timeProvider);
    }

    public event EventHandler? Changed;
    public event EventHandler<BoardTraceEntry>? Traced;
    public BoardModel Model => SmartDenModels.Ip16RMt;
    public TimeProvider TimeProvider { get; }
    public int Port { get; private set; }
    public byte UnitId { get; set; } = SmartDenApi.DefaultUnitId;
    public bool HasLogin => false;
    public bool RejectConnections { get; set; }
    public bool NoAnswer { get; set; }
    public ulong FailRelays { get; set; }
    public int UnansweredCount => Volatile.Read(ref _unanswered);
    public int ConnectionCount => _connection is { Connected: true } ? 1 : 0;
    public ulong Relays { get { lock (_lock) return _relays; } }
    public ChannelMask Pulsing => new(_pulses.Running);

    public SimulatedBoardState State
    {
        get
        {
            lock (_lock)
            {
                IReadOnlyList<string> clients = _connection is { Connected: true } tcp
                    ? [tcp.Client.RemoteEndPoint?.ToString() ?? "unknown"] : [];
                return new SimulatedBoardState(new ChannelMask(_relays), ChannelMask.None, clients);
            }
        }
    }

    public void Start(int port = 0)
    {
        if (_listener is not null) throw new InvalidOperationException("The simulator is already started.");
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_listener, _stopping.Token);
        _logger.LogInformation("Simulating a smartDEN IP-16R-MT on port {Port}", Port);
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        var bit = Model.Capabilities.Relay(relay).Bits;
        lock (_lock)
        {
            _pulses.Cancel(bit);
            _relays = turnOn ? _relays | bit : _relays & ~bit;
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed) =>
        throw new ArgumentOutOfRangeException(nameof(input), "The smartDEN IP-16R-MT has no digital inputs.");

    public void DropConnections() => Volatile.Read(ref _connection)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        _pulses.Dispose();
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        DropConnections();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        _listener?.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (RejectConnections || _connection is { Connected: true })
                {
                    tcp.Dispose();
                    continue;
                }

                _connection = tcp;
                RaiseChanged();
                Trace(BoardTraceDirection.None, "The app connected");
                await HandleConnectionAsync(tcp, cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(_connection, tcp)) _connection = null;
                RaiseChanged();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        using (tcp)
        {
            try
            {
                var stream = tcp.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (frame is null) return;
                    var response = Handle(frame);
                    if (response is null)
                    {
                        Interlocked.Increment(ref _unanswered);
                        continue;
                    }
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Client disconnected or simulator stopping.
            }
        }
    }

    private static async Task<byte[]?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length is < 2 or > 254 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0) return null;
            var pdu = new byte[length - 1];
            await stream.ReadExactlyAsync(pdu, cancellationToken).ConfigureAwait(false);
            var frame = new byte[7 + pdu.Length];
            header.CopyTo(frame, 0);
            pdu.CopyTo(frame, 7);
            return frame;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private byte[]? Handle(byte[] frame)
    {
        var transaction = BinaryPrimitives.ReadUInt16BigEndian(frame);
        var unit = frame[6];
        var pdu = frame.AsSpan(7);
        if (unit != UnitId || pdu.Length == 0 || NoAnswer)
        {
            Trace(BoardTraceDirection.None, "No answer to the request", SmartDenModbus.Hex(frame), true);
            return null;
        }

        var function = pdu[0];
        var reply = function switch
        {
            SmartDenApi.ReadCoils => ReadCoils(pdu),
            SmartDenApi.WriteSingleCoil => WriteCoil(pdu),
            SmartDenApi.WriteSingleRegister => Pulse(pdu),
            _ => Exception(function, 0x01),
        };
        Trace(BoardTraceDirection.Sent, Describe(function, pdu), SmartDenModbus.Hex(frame));
        var response = SmartDenModbus.Response(transaction, unit, reply);
        Trace(BoardTraceDirection.Received, "Modbus TCP response", SmartDenModbus.Hex(response));
        return response;
    }

    private byte[] ReadCoils(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(SmartDenApi.ReadCoils, 0x03);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count is < 1 or > SmartDenApi.RelayCount) return Exception(SmartDenApi.ReadCoils, 0x03);
        if (address + count > SmartDenApi.RelayCount) return Exception(SmartDenApi.ReadCoils, 0x02);
        ulong bits;
        lock (_lock) bits = (_relays >> address) & ((1UL << count) - 1);
        return [SmartDenApi.ReadCoils, 2, (byte)bits, (byte)(bits >> 8)];
    }

    private byte[] WriteCoil(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(SmartDenApi.WriteSingleCoil, 0x03);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (address >= SmartDenApi.RelayCount) return Exception(SmartDenApi.WriteSingleCoil, 0x02);
        if (value is not (0xFF00 or 0x0000 or 0xFF02)) return Exception(SmartDenApi.WriteSingleCoil, 0x03);
        var bit = 1UL << address;
        lock (_lock)
        {
            if ((FailRelays & bit) != 0) return Exception(SmartDenApi.WriteSingleCoil, 0x04);
            _pulses.Cancel(bit);
            _relays = value switch { 0xFF00 => _relays | bit, 0x0000 => _relays & ~bit, _ => _relays ^ bit };
        }
        RaiseChanged();
        return [.. pdu];
    }

    private byte[] Pulse(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(SmartDenApi.WriteSingleRegister, 0x03);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var duration = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (address >= SmartDenApi.RelayCount) return Exception(SmartDenApi.WriteSingleRegister, 0x02);
        if (duration == 0) return Exception(SmartDenApi.WriteSingleRegister, 0x03);
        var bit = 1UL << address;
        lock (_lock)
        {
            if ((FailRelays & bit) != 0) return Exception(SmartDenApi.WriteSingleRegister, 0x04);
            _relays |= bit;
            _pulses.Start(bit, TimeSpan.FromTicks(duration * SmartDenApi.PulseUnit.Ticks), take =>
            {
                lock (_lock) _relays &= ~take();
                RaiseChanged();
            });
        }
        RaiseChanged();
        return [.. pdu];
    }

    private static byte[] Exception(byte function, byte code) => [(byte)(function | SmartDenApi.ExceptionFlag), code];

    private static string Describe(byte function, ReadOnlySpan<byte> pdu)
    {
        if (function == SmartDenApi.ReadCoils) return "Read relay states";
        var relay = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]) + 1;
        return function == SmartDenApi.WriteSingleRegister ? $"Pulse relay {relay}" : $"Set relay {relay}";
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false) =>
        Traced?.Invoke(this, new BoardTraceEntry(TimeProvider.GetUtcNow(), direction, text, bytes, isError, false));

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
