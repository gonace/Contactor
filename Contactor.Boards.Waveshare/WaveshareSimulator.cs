using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// A fake Waveshare Modbus PoE ETH relay of any of the five models, on a local TCP port, speaking Modbus RTU over TCP
/// as the board does (<see cref="WaveshareApi"/>): its relays (FC 01, 05), inputs (FC 02) and, on the models with
/// inputs, the relay control mode registers (FC 03, 06, 10), with the relays following their inputs in linkage, toggle
/// and edge mode, and "flash on" (FC 05 at 0x0200 + relay − 1), which turns a relay off again after its time on
/// <see cref="TimeProvider"/>. A request with a bad address, quantity or value gets a Modbus exception answer; one with a bad CRC
/// or for another unit gets none, as on a serial Modbus line. It never tells the app anything by itself, as the real
/// board. Several clients may connect at once. Used by the tests and, in development, by Contactor.Simulator. It
/// listens on the loopback interface only.
/// </summary>
public sealed class WaveshareSimulator : ISimulatedBoard
{
    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, string> _connections = new();
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly Lock _lock = new();
    private readonly ushort[] _modes = new ushort[WaveshareApi.RelayModeCount];
    private SimulatedPulses? _pulses;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ulong _relays;
    private ulong _inputs;
    private ulong? _lastRelaysSent;
    private ulong? _lastInputsSent;
    private int _requestCount;
    private int _unansweredCount;

    public WaveshareSimulator(BoardModel model)
        : this(model, NullLogger.Instance)
    {
    }

    public WaveshareSimulator(BoardModel model, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        if (!WaveshareModels.IsWaveshare(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Waveshare board.", nameof(model));
        }

        Model = model;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model { get; }

    /// <summary>The clock for the traffic's times and the end of a flash.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The relays whose flash is still running.</summary>
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

    /// <summary>Never: Modbus has no login.</summary>
    public bool HasLogin => false;

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

    /// <summary>The relay control modes of relays 1 to 8 (0 normal, 1 linkage, 2 toggle, 3 edge); all 0 without inputs.</summary>
    public IReadOnlyList<ushort> RelayModes
    {
        get
        {
            lock (_lock)
            {
                return [.. _modes];
            }
        }
    }

    /// <summary>Requests answered since start, exception answers included.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Requests left without an answer: a bad CRC, another unit, or <see cref="NoAnswer"/>.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>How many connections are open.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>When true, requests are read but never answered, as if the board hung.</summary>
    public bool NoAnswer { get; set; }

    /// <summary>
    /// Relays (bit 0 = relay 1) whose FC 05 is answered with exception 04 and not carried out, for the tests of a
    /// refused command.
    /// </summary>
    public ulong FailRelays { get; set; }

    /// <summary>When true, reads of the relay modes (FC 03) are answered with exception 04, for the tests.</summary>
    public bool RefuseModeReads { get; set; }

    /// <summary>When true, writes of the relay modes (FC 06, FC 10) are answered with exception 04, for the tests.</summary>
    public bool RefuseModeWrites { get; set; }

    /// <summary>The requests received, as hex, e.g. "01 05 00 00 FF 00 8C 3A". The newest 1,000.</summary>
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
        Change(() =>
        {
            Pulses.Cancel(bit);
            _relays = turnOn ? _relays | bit : _relays & ~bit;
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets a digital input; a relay whose control mode ties it to that input follows it as on the board: linkage
    /// copies the input, toggle flips the relay when the input closes, edge flips it on every change.
    /// </summary>
    public Task SetInputAsync(int input, bool closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Model.Capabilities.InputCount);
        var bit = 1UL << (input - 1);
        Change(() =>
        {
            var was = (_inputs & bit) != 0;
            _inputs = closed ? _inputs | bit : _inputs & ~bit;
            if (was == closed || input > WaveshareApi.RelayModeCount || input > Model.Capabilities.RelayCount)
            {
                return;
            }

            switch (_modes[input - 1])
            {
                case WaveshareApi.ModeLinkage:
                    _relays = closed ? _relays | bit : _relays & ~bit;
                    break;
                case WaveshareApi.ModeToggle when closed:
                case WaveshareApi.ModeEdge:
                    _relays ^= bit;
                    break;
                default:
                    break;
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Sets a relay's control mode as the board's own tool (Vircom) or web page would.</summary>
    public Task SetRelayModeAsync(int relay, ushort mode)
    {
        if (!WaveshareModels.HasRelayModes(Model))
        {
            throw new InvalidOperationException($"The {Model.Name} has no relay control modes.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, WaveshareApi.RelayModeCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, WaveshareApi.ModeEdge);
        lock (_lock)
        {
            _modes[relay - 1] = mode;
        }

        RaiseChanged();
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
        try
        {
            var stream = tcp.GetStream();
            while (true)
            {
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var answer = Handle(request);
                if (answer is null)
                {
                    Interlocked.Increment(ref _unansweredCount);
                    continue;
                }

                Interlocked.Increment(ref _requestCount);
                await stream.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
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

    // One request frame, its length taken from its function as a Modbus device does; null when the client closed.
    private static async Task<byte[]?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new byte[8];
        if (!await ReadAsync(stream, head.AsMemory(0, 2), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (head[1] is WaveshareApi.WriteMultipleCoils or WaveshareApi.WriteMultipleRegisters)
        {
            // Unit, function, address, quantity, byte count; then the data and the CRC.
            if (!await ReadAsync(stream, head.AsMemory(2, 5), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var frame = new byte[7 + head[6] + 2];
            head.AsSpan(0, 7).CopyTo(frame);
            return await ReadAsync(stream, frame.AsMemory(7), cancellationToken).ConfigureAwait(false) ? frame : null;
        }

        // Every other function the app could send is 8 bytes; an unknown one is taken as 8 bytes too.
        return await ReadAsync(stream, head.AsMemory(2), cancellationToken).ConfigureAwait(false) ? head : null;
    }

    private static async Task<bool> ReadAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    // The answer, or null to send none.
    private byte[]? Handle(byte[] frame)
    {
        var hex = WaveshareModbus.Hex(frame);
        Remember(hex);
        var function = frame[1];
        Trace(BoardTraceDirection.Sent, WaveshareTraceText.Request(frame), hex, isRoutine: function is WaveshareApi.ReadCoils or WaveshareApi.ReadDiscreteInputs);
        if (!WaveshareModbus.HasValidCrc(frame))
        {
            return Silent("No answer: the CRC is wrong");
        }

        if (frame[0] != WaveshareApi.UnitId)
        {
            return Silent("No answer: the request is for another unit");
        }

        if (NoAnswer)
        {
            return Silent("No answer: the board hangs");
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
        var capabilities = Model.Capabilities;
        return function switch
        {
            WaveshareApi.ReadCoils => ReadBits(function, address, value, capabilities.RelayCount, () => _relays, ref _lastRelaysSent, WaveshareTraceText.Relays),
            WaveshareApi.ReadDiscreteInputs when capabilities.InputCount > 0 =>
                ReadBits(function, address, value, capabilities.InputCount, () => _inputs, ref _lastInputsSent, WaveshareTraceText.Inputs),
            WaveshareApi.ReadHoldingRegisters when WaveshareModels.HasRelayModes(Model) && RefuseModeReads => Refuse(function, WaveshareApi.DeviceFailure),
            WaveshareApi.WriteSingleRegister or WaveshareApi.WriteMultipleRegisters when WaveshareModels.HasRelayModes(Model) && RefuseModeWrites =>
                Refuse(function, WaveshareApi.DeviceFailure),
            WaveshareApi.ReadHoldingRegisters when WaveshareModels.HasRelayModes(Model) => ReadModes(address, value),
            WaveshareApi.WriteSingleCoil when WaveshareModbus.IsFlashOn(address) => FlashOn(frame, address, value),
            WaveshareApi.WriteSingleCoil => WriteCoil(frame, address, value),
            WaveshareApi.WriteSingleRegister when WaveshareModels.HasRelayModes(Model) => WriteModes(frame, address, [value]),
            WaveshareApi.WriteMultipleRegisters when WaveshareModels.HasRelayModes(Model) => WriteRegisters(frame, address, value),
            WaveshareApi.ReadHoldingRegisters or WaveshareApi.WriteSingleRegister or WaveshareApi.WriteMultipleRegisters => Refuse(function, WaveshareApi.IllegalDataAddress),
            _ => Refuse(function, WaveshareApi.IllegalFunction),
        };
    }

    private delegate string Describe(ulong bits, int count);

    private byte[] ReadBits(byte function, ushort address, ushort count, int channels, Func<ulong> read, ref ulong? lastSent, Describe describe)
    {
        if (count is < 1 or > WaveshareModbus.MaxReadBits)
        {
            return Refuse(function, WaveshareApi.IllegalDataValue);
        }

        if (address + count > channels)
        {
            return Refuse(function, WaveshareApi.IllegalDataAddress);
        }

        ulong bits;
        bool routine;
        lock (_lock)
        {
            bits = (read() >> address) & ChannelMask.FirstN(count).Bits;
            routine = lastSent == bits;
            lastSent = bits;
        }

        var bytes = (count + 7) / 8;
        var body = new byte[3 + bytes];
        body[0] = WaveshareApi.UnitId;
        body[1] = function;
        body[2] = (byte)bytes;
        for (var i = 0; i < bytes; i++)
        {
            body[3 + i] = (byte)(bits >> (8 * i));
        }

        return Answer(body, describe(bits, count), routine);
    }

    private byte[] ReadModes(ushort address, ushort count)
    {
        if (count is < 1 or > WaveshareModbus.MaxReadRegisters)
        {
            return Refuse(WaveshareApi.ReadHoldingRegisters, WaveshareApi.IllegalDataValue);
        }

        if (!InModeRange(address, count))
        {
            return Refuse(WaveshareApi.ReadHoldingRegisters, WaveshareApi.IllegalDataAddress);
        }

        ushort[] modes;
        lock (_lock)
        {
            modes = _modes[(address - WaveshareApi.RelayModeRegister)..(address - WaveshareApi.RelayModeRegister + count)];
        }

        var body = new byte[3 + (count * 2)];
        body[0] = WaveshareApi.UnitId;
        body[1] = WaveshareApi.ReadHoldingRegisters;
        body[2] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3 + (i * 2)), modes[i]);
        }

        return Answer(body, address == WaveshareApi.RelayModeRegister ? WaveshareTraceText.Modes(modes) : "Relay modes", routine: false);
    }

    // FC 05: echo. 0x00FF is every relay at once, as on the board. A relay in linkage mode follows its input only.
    private byte[] WriteCoil(byte[] frame, ushort address, ushort value)
    {
        if (value is not (WaveshareApi.CoilOn or WaveshareApi.CoilOff or WaveshareApi.CoilToggle))
        {
            return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.IllegalDataValue);
        }

        var all = Model.Capabilities.AllRelays.Bits;
        ulong bits;
        if (address == 0x00FF)
        {
            bits = all;
        }
        else if (address < Model.Capabilities.RelayCount)
        {
            bits = 1UL << address;
            if ((FailRelays & bits) != 0)
            {
                return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.DeviceFailure);
            }
        }
        else
        {
            return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.IllegalDataAddress);
        }

        Change(() =>
        {
            Pulses.Cancel(bits);
            var free = bits & ~Linked();
            _relays = value switch
            {
                WaveshareApi.CoilOn => _relays | free,
                WaveshareApi.CoilOff => _relays & ~free,
                _ => _relays ^ free,
            };
        });
        return Answer(frame.AsSpan(0, 6).ToArray(), WaveshareTraceText.Confirmed(frame), routine: false);
    }

    // FC 05 "flash on": the relay on now, off again after value × 100 ms; echo. A relay in linkage mode follows its input
    // only. A later FC 05 for the relay ends the flash, or starts it again.
    private byte[] FlashOn(byte[] frame, ushort address, ushort value)
    {
        var relay = address - WaveshareApi.FlashOnAddress;
        if (relay >= Model.Capabilities.RelayCount)
        {
            return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.IllegalDataAddress);
        }

        if (value is < 1 or > WaveshareApi.MaxFlashTime)
        {
            return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.IllegalDataValue);
        }

        var bit = 1UL << relay;
        if ((FailRelays & bit) != 0)
        {
            return Refuse(WaveshareApi.WriteSingleCoil, WaveshareApi.DeviceFailure);
        }

        // All under the state lock, and the end takes its relays under it too, so a command that came first wins
        // (see SimulatedPulses).
        Change(() =>
        {
            Pulses.Cancel(bit);
            var free = bit & ~Linked();
            _relays |= free;
            Pulses.Start(free, WaveshareApi.FlashUnit * value, take => Change(() => _relays &= ~take()));
        });
        return Answer(frame.AsSpan(0, 6).ToArray(), WaveshareTraceText.Confirmed(frame), routine: false);
    }

    private byte[] WriteRegisters(byte[] frame, ushort address, ushort count)
    {
        if (count is < 1 or > WaveshareModbus.MaxWriteRegisters || frame[6] != count * 2)
        {
            return Refuse(WaveshareApi.WriteMultipleRegisters, WaveshareApi.IllegalDataValue);
        }

        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(7 + (i * 2)));
        }

        return WriteModes(frame, address, values);
    }

    // FC 06 and FC 10 on the mode registers: modes 0–3 only. FC 06 echoes the request, FC 10 its address and quantity.
    private byte[] WriteModes(byte[] frame, ushort address, ushort[] values)
    {
        var function = frame[1];
        if (!InModeRange(address, values.Length))
        {
            return Refuse(function, WaveshareApi.IllegalDataAddress);
        }

        if (values.Any(v => v > WaveshareApi.ModeEdge))
        {
            return Refuse(function, WaveshareApi.IllegalDataValue);
        }

        lock (_lock)
        {
            values.CopyTo(_modes, address - WaveshareApi.RelayModeRegister);
        }

        RaiseChanged();
        return Answer(frame.AsSpan(0, 6).ToArray(), WaveshareTraceText.Confirmed(frame), routine: false);
    }

    private static bool InModeRange(ushort address, int count)
    {
        return address >= WaveshareApi.RelayModeRegister && address + count <= WaveshareApi.RelayModeRegister + WaveshareApi.RelayModeCount;
    }

    // Under _lock: the relays that follow their input and take no command.
    private ulong Linked()
    {
        ulong linked = 0;
        for (var i = 0; i < _modes.Length; i++)
        {
            if (_modes[i] == WaveshareApi.ModeLinkage)
            {
                linked |= 1UL << i;
            }
        }

        return linked;
    }

    private byte[] Refuse(byte function, byte code)
    {
        return Answer([WaveshareApi.UnitId, (byte)(function | WaveshareApi.ExceptionFlag), code], WaveshareTraceText.Refused(code), routine: false, isError: true);
    }

    private byte[] Answer(byte[] body, string text, bool routine, bool isError = false)
    {
        var frame = WaveshareModbus.WithCrc(body);
        Trace(BoardTraceDirection.Received, text, WaveshareModbus.Hex(frame), isError, routine);
        return frame;
    }

    private byte[]? Silent(string text)
    {
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
}
