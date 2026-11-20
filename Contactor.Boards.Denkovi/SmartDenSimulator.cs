using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// A fake Denkovi smartDEN IP-16R-MT on a local TCP port, speaking Modbus TCP as the board does (<see cref="SmartDenApi"/>):
/// its 16 relays (FC 01, 05, 0F) and their pulses (FC 06 at 0x0000 + relay − 1), which turn a relay off again after
/// its time on <see cref="TimeProvider"/>. A request with a bad address, quantity, value or length gets a Modbus
/// exception answer; one for another protocol gets none. A frame whose header gives a length no Modbus frame has
/// closes the connection, as nothing after it can be read in step. Of the configuration registers (table 10.7) it holds
/// the week schedule's: the start date and the rows' six blocks, read with FC 03 and written with FC 06 and FC 10, a
/// request inside one block only. It keeps them over a power cut. A row that is on can run now
/// (<see cref="ISimulatedBoardRules"/>), as when its time comes. It takes one connection at a time, as the manual says the board does: a new one is
/// closed at once while another is open, or, with <see cref="NewConnectionTakesOver"/>, closes the one before (the manual
/// doesn't say which; see README.md). It never tells the app anything by itself, as the real board. Used by the tests and, in
/// development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class SmartDenSimulator : ISimulatedBoard, ISimulatedBoardRejectConnections, ISimulatedBoardNoAnswer, ISimulatedBoardFailRelays, ISimulatedBoardPulses, ISimulatedBoardPower, ISimulatedBoardSlowAnswers, ISimulatedBoardRules
{
    // The week schedule's register blocks (table 10.7): where each starts, and the values each register takes. A
    // request must stay inside one block (section 10.2.1.3).
    private static readonly (ushort Start, ushort Min, ushort Max)[][] BlockRanges =
    [
        [(SmartDenApi.ScheduleStartDateRegister, 1, 31), (0, 1, 12), (0, 2000, 2099)],
        Block(SmartDenApi.RowEnableRegister, 0, 1),
        Block(SmartDenApi.RowOutputsRegister, 0, ushort.MaxValue),
        Block(SmartDenApi.RowStateRegister, 0, 1),
        Block(SmartDenApi.RowHourRegister, 0, 23),
        Block(SmartDenApi.RowMinuteRegister, 0, 59),
        Block(SmartDenApi.RowWeekDaysRegister, 0, 127),
    ];

    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, string> _connections = new();
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly Lock _lock = new();
    private readonly Lock _connectionsLock = new();
    private SimulatedPulses? _pulses;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ulong _relays;
    private int _poweredOff;
    private long _answerDelayTicks;
    private ulong _beforeCut;
    private ulong? _lastRelaysSent;
    private int _requestCount;
    private int _unansweredCount;

    // The week schedule's registers, by address; only under _lock. The start date as in the manual's example (table
    // 10.22, 10/09/2020), as the manual gives no factory value; every row 0.
    private readonly Dictionary<ushort, ushort> _registers = NewRegisters();

    public SmartDenSimulator()
        : this(NullLogger.Instance)
    {
    }

    public SmartDenSimulator(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model => SmartDenModels.Ip16RMt;

    /// <summary>The clock for the traffic's times and the end of a pulse.</summary>
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

    /// <summary>Never: Modbus has no login.</summary>
    public bool HasLogin => false;

    /// <summary>Its relays; it has no inputs, and the client is the open connection.</summary>
    public SimulatedBoardState State
    {
        get
        {
            lock (_lock)
            {
                return new SimulatedBoardState(
                    new ChannelMask(_relays),
                    ChannelMask.None,
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

    /// <summary>Requests answered since start, exception answers included.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Requests left without an answer: another protocol, or <see cref="NoAnswer"/>.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>How many connections are open: 0 or 1.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>
    /// When true, a new connection closes the one open before; when false (the default), a new connection is closed at
    /// once while another is open. A connection whose client closed it doesn't count as open.
    /// </summary>
    public bool NewConnectionTakesOver { get; set; }

    /// <summary>When true, requests are read but never answered, as if the board hung.</summary>
    public bool NoAnswer { get; set; }

    /// <summary>
    /// Relays (bit 0 = relay 1) whose FC 05, FC 06 and FC 0F are answered with exception 04 and not carried out, for
    /// the tests of a refused command.
    /// </summary>
    public ulong FailRelays { get; set; }

    /// <summary>When true, FC 03 is refused with code 1, as by a firmware without it: the app can't read the schedule.</summary>
    public bool RefuseScheduleReads { get; set; }

    /// <summary>When true, FC 06 and FC 10 at the week schedule are refused with code 1: the app can't turn a row off.</summary>
    public bool RefuseScheduleWrites { get; set; }

    /// <summary>The rows that are enabled or have relays, in row order.</summary>
    public IReadOnlyList<SmartDenSimulatedRow> ScheduleRows
    {
        get
        {
            lock (_lock)
            {
                return [.. Enumerable.Range(0, SmartDenApi.ScheduleRows).Select(RowAt).Where(r => r.Enabled || r.Relays != 0)];
            }
        }
    }

    /// <summary>A register as the board holds it; throws for an address it doesn't hold.</summary>
    public ushort Register(ushort address)
    {
        lock (_lock)
        {
            return _registers.TryGetValue(address, out var value)
                ? value
                : throw new ArgumentOutOfRangeException(nameof(address), address, "Not a register the simulator holds.");
        }
    }

    /// <summary>
    /// Sets a whole row as the board's own web page would (section 8.9), and tells the page. Nothing while the power is
    /// cut. Throws <see cref="ArgumentOutOfRangeException"/> for a row or a value the board doesn't take.
    /// </summary>
    public Task SetRowAsync(SmartDenSimulatedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Row, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row.Row, SmartDenApi.ScheduleRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Hour, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row.Hour, 23);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Minute, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row.Minute, 59);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.WeekDays, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row.WeekDays, 127);
        ChangeSchedule(() =>
        {
            var n = (ushort)row.Row;
            _registers[(ushort)(SmartDenApi.RowEnableRegister + n)] = row.Enabled ? SmartDenApi.RowEnabled : SmartDenApi.RowDisabled;
            _registers[(ushort)(SmartDenApi.RowOutputsRegister + n)] = row.Relays;
            _registers[(ushort)(SmartDenApi.RowStateRegister + n)] = (ushort)(row.TurnOn ? 1 : 0);
            _registers[(ushort)(SmartDenApi.RowHourRegister + n)] = (ushort)row.Hour;
            _registers[(ushort)(SmartDenApi.RowMinuteRegister + n)] = (ushort)row.Minute;
            _registers[(ushort)(SmartDenApi.RowWeekDaysRegister + n)] = (ushort)row.WeekDays;
        });
        return Task.CompletedTask;
    }

    /// <summary>Turns one row on or off, leaving its relays and time; nothing while the power is cut.</summary>
    public Task SetRowEnabledAsync(int row, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, SmartDenApi.ScheduleRows);
        ChangeSchedule(() => _registers[(ushort)(SmartDenApi.RowEnableRegister + row)] = enabled ? SmartDenApi.RowEnabled : SmartDenApi.RowDisabled);
        return Task.CompletedTask;
    }

    /// <summary>The first row that is off and has no relays, for a new one; null when all 30 are used.</summary>
    public int? FreeRow => Enumerable.Range(0, SmartDenApi.ScheduleRows).Cast<int?>().FirstOrDefault(r => ScheduleRows.All(row => row.Row != r));

    /// <summary>The rows that are on and switch a relay, which the board runs when their time comes.</summary>
    public IReadOnlyList<SimulatedRule> RunnableRules => [.. ScheduleRows
        .Where(r => r.Enabled && r.Relays != 0)
        .Select(r => new SimulatedRule(
            string.Create(CultureInfo.InvariantCulture, $"row-{r.Row}"),
            string.Create(CultureInfo.InvariantCulture, $"Row {r.Row}: {r.Text}"),
            [.. new ChannelMask(r.Relays).Channels.Select(relay => new SimulatedRuleSwitch(relay, r.TurnOn))]))];

    /// <summary>Runs a row now: its relays on or off as its state says, as at its time.</summary>
    public Task<SimulatedRule?> RunRuleAsync(string id)
    {
        if (IsPoweredOff || RunnableRules.FirstOrDefault(r => r.Id == id) is not { } rule)
        {
            return Task.FromResult<SimulatedRule?>(null);
        }

        var mask = ChannelMask.Of(rule.Switches.Select(s => s.Relay)).Bits;
        var on = rule.Switches.All(s => s.TurnOn);
        Change(() =>
        {
            Pulses.Cancel(mask);
            _relays = on ? _relays | mask : _relays & ~mask;
        });
        return Task.FromResult<SimulatedRule?>(rule);
    }

    /// <summary>The requests received, as hex, e.g. "00 01 00 00 00 06 00 05 00 00 FF 00". The newest 1,000.</summary>
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

    /// <summary>Throws: the smartDEN IP-16R-MT has no inputs.</summary>
    public Task SetInputAsync(int input, bool closed)
    {
        throw new ArgumentOutOfRangeException(nameof(input), input, $"The {Model.Name} has no inputs.");
    }

    /// <summary>How long the board waits, on its own clock, before each answer it sends; zero (the default) answers at once.</summary>
    public TimeSpan AnswerDelay
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _answerDelayTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Volatile.Write(ref _answerDelayTicks, value.Ticks);
        }
    }

    /// <summary>Whether the power is cut: nothing answers, and every relay is off.</summary>
    public bool IsPoweredOff => Volatile.Read(ref _poweredOff) != 0;

    public string AfterPowerCut => SaveOutputs ? "Save Outputs is on: the relays come back as they were." : "Save Outputs is off: every relay comes back off.";

    /// <summary>"Save Outputs" (section 8.2): the board sets the relays again after a restart as they were; off, they come back off. Off by default here, as the manual gives no factory value.</summary>
    public bool SaveOutputs { get; set; }

    /// <summary>Cuts the power: connections drop, new ones are closed at once, pulses end and every relay goes off.</summary>
    public Task CutPowerAsync()
    {
        if (Interlocked.Exchange(ref _poweredOff, 1) != 0)
        {
            return Task.CompletedTask;
        }

        DropConnections();
        Change(() =>
        {
            Pulses.Cancel(ulong.MaxValue);
            _beforeCut = _relays;
            _relays = 0UL;
        }, power: true);
        return Task.CompletedTask;
    }

    /// <summary>Gives the power back: the relays as the board sets them after a power cut (<see cref="AfterPowerCut"/>), and it answers again.</summary>
    public Task RestorePowerAsync()
    {
        if (!IsPoweredOff)
        {
            return Task.CompletedTask;
        }

        Change(
            () =>
            {
                _relays = SaveOutputs ? _beforeCut : 0UL;
                Volatile.Write(ref _poweredOff, 0);
            },
            power: true);
        return Task.CompletedTask;
    }

    public void DropConnections()
    {
        lock (_connectionsLock)
        {
            foreach (var connection in _connections.Keys)
            {
                Remove(connection);
            }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset
                && !cancellationToken.IsCancellationRequested)
            {
                // A client that went away before it was accepted: the next one is still welcome.
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
                break;
            }
            catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped before the loop got to accept.
                break;
            }

            handlers.RemoveAll(h => h.IsCompleted);
            if (RejectConnections || IsPoweredOff)
            {
                tcp.Dispose();
                continue;
            }

            try
            {
                if (Admit(tcp) is { } remote)
                {
                    if (IsPoweredOff)
                    {
                        // The power went just as it came: DropConnections may have missed it.
                        Remove(tcp);
                        continue;
                    }

                    handlers.Add(HandleConnectionAsync(tcp, remote, cancellationToken));
                }
            }
#pragma warning disable CA1031 // One connection that can't be taken must not stop the board taking the next.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Remove(tcp);
                tcp.Dispose();
                _logger.SimulatorConnectionFailed(ex);
            }
        }

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    // One connection at a time, as the board: while one is open, the new one is closed at once, or takes its place
    // (NewConnectionTakesOver). Returns where it comes from, or null when it was closed.
    private string? Admit(TcpClient tcp)
    {
        var remote = RemoteOf(tcp);
        lock (_connectionsLock)
        {
            foreach (var earlier in _connections.Keys)
            {
                if (IsClosedByClient(earlier))
                {
                    Remove(earlier);
                }
                else if (NewConnectionTakesOver)
                {
                    Remove(earlier);
                    Trace(BoardTraceDirection.None, "Closed the connection before: one connection at a time", null);
                }
                else
                {
                    tcp.Dispose();
                    Trace(BoardTraceDirection.None, $"Refused a connection from {remote}: one connection at a time", null, isError: true);
                    return null;
                }
            }

            _connections[tcp] = remote;
        }

        RaiseChanged();
        Trace(BoardTraceDirection.None, $"The app connected from {remote}", null);
        return remote;
    }

    // Whether the client closed the connection, though its handler hasn't seen that yet: readable with nothing to read.
    // A connection its handler closed meanwhile has no socket any more.
    private static bool IsClosedByClient(TcpClient tcp)
    {
        try
        {
            return tcp.Client is not { } socket || (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static string RemoteOf(TcpClient tcp)
    {
        try
        {
            return tcp.Client?.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return "unknown";
        }
    }

    private void Remove(TcpClient tcp)
    {
        if (_connections.TryRemove(tcp, out _))
        {
            tcp.Dispose();
            RaiseChanged();
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, string remote, CancellationToken cancellationToken)
    {
        // Let the accept loop go on at once.
        await Task.Yield();
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
                await DelayAnswerAsync(cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The client went away, a new connection took its place, or the simulator is stopping.
        }
#pragma warning disable CA1031 // Whatever one connection sends must not take down the simulated board or its other work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.SimulatorConnectionFailed(ex);
            Trace(BoardTraceDirection.None, $"Closed the connection from {remote}: {ex.Message}", null, isError: true);
        }
        finally
        {
            Remove(tcp);
            tcp.Dispose();
        }
    }

    // One request frame, its length taken from its header as a Modbus TCP server does; null when the client closed, or
    // when the header gives a length no Modbus frame has: nothing after it can be read in step, so the connection ends.
    private async Task<byte[]?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new byte[SmartDenApi.HeaderBytes];
        if (!await ReadAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (length is < 2 or > SmartDenApi.MaxLength)
        {
            Remember(SmartDenModbus.Hex(head));
            Trace(
                BoardTraceDirection.Sent,
                string.Create(CultureInfo.InvariantCulture, $"Closed the connection: a frame of length {length}"),
                SmartDenModbus.Hex(head),
                isError: true);
            return null;
        }

        var frame = new byte[SmartDenApi.HeaderBytes - 1 + length];
        head.CopyTo(frame, 0);
        return await ReadAsync(stream, frame.AsMemory(SmartDenApi.HeaderBytes), cancellationToken).ConfigureAwait(false) ? frame : null;
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

    // The answer to a whole frame (at least the header and a function code), or null to send none.
    private byte[]? Handle(byte[] frame)
    {
        var hex = SmartDenModbus.Hex(frame);
        Remember(hex);
        var pdu = frame.AsSpan(SmartDenApi.HeaderBytes);
        var function = pdu[0];
        Trace(BoardTraceDirection.Sent, SmartDenTraceText.Request(frame), hex, isRoutine: function == SmartDenApi.ReadCoils);
        var protocol = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        if (protocol != SmartDenApi.ProtocolId)
        {
            return Silent(string.Create(CultureInfo.InvariantCulture, $"No answer: protocol {protocol} isn't Modbus"));
        }

        if (NoAnswer)
        {
            return Silent("No answer: the board hangs");
        }

        var request = new Request(BinaryPrimitives.ReadUInt16BigEndian(frame), frame[6], function);
        return function switch
        {
            SmartDenApi.ReadCoils => ReadCoils(request, pdu),
            SmartDenApi.WriteSingleCoil => WriteCoil(request, pdu),
            SmartDenApi.WriteSingleRegister => WriteRegister(request, pdu),
            SmartDenApi.WriteMultipleCoils => WriteCoils(request, pdu),
            SmartDenApi.ReadHoldingRegisters => ReadRegisters(request, pdu),
            SmartDenApi.WriteMultipleRegisters => WriteRegisters(request, pdu),
            _ => Refuse(request, SmartDenApi.IllegalFunction),
        };
    }

    // FC 01: the function, address and quantity; at most the 16 relays.
    private byte[] ReadCoils(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count is < 1 or > SmartDenModbus.MaxReadBits)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        if (address + count > SmartDenApi.RelayCount)
        {
            return Refuse(request, SmartDenApi.IllegalDataAddress);
        }

        ulong bits;
        bool routine;
        lock (_lock)
        {
            bits = (_relays >> address) & ChannelMask.FirstN(count).Bits;
            routine = _lastRelaysSent == bits;
            _lastRelaysSent = bits;
        }

        var bytes = (count + 7) / 8;
        var answer = new byte[2 + bytes];
        answer[0] = SmartDenApi.ReadCoils;
        answer[1] = (byte)bytes;
        for (var i = 0; i < bytes; i++)
        {
            answer[2 + i] = (byte)(bits >> (8 * i));
        }

        return Answer(request, answer, SmartDenTraceText.Relays(bits, count), routine);
    }

    // FC 05: echo. FF00 on, 0000 off, FF02 flips; it ends a pulse of the relay.
    private byte[] WriteCoil(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (address >= SmartDenApi.RelayCount)
        {
            return Refuse(request, SmartDenApi.IllegalDataAddress);
        }

        if (value is not (SmartDenApi.CoilOn or SmartDenApi.CoilOff or SmartDenApi.CoilToggle))
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var bit = 1UL << address;
        if ((FailRelays & bit) != 0)
        {
            return Refuse(request, SmartDenApi.DeviceFailure);
        }

        Change(() =>
        {
            Pulses.Cancel(bit);
            _relays = value switch
            {
                SmartDenApi.CoilOn => _relays | bit,
                SmartDenApi.CoilOff => _relays & ~bit,
                _ => _relays ^ bit,
            };
        });
        return Answer(request, pdu.ToArray(), SmartDenTraceText.Confirmed(request.Frame(pdu)), routine: false);
    }

    // FC 06: a pulse register, or a week schedule register. A pulse turns the relay on now, off again after value × 100
    // ms; echo. A later FC 05 for the relay ends the pulse, a later pulse starts it again.
    private byte[] WriteRegister(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (!SmartDenModbus.IsPulse(address))
        {
            return WriteScheduleRegisters(request, pdu, address, [value], echo: pdu.ToArray());
        }

        if (value < 1)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var bit = 1UL << (address - SmartDenApi.PulseRegister);
        if ((FailRelays & bit) != 0)
        {
            return Refuse(request, SmartDenApi.DeviceFailure);
        }

        // All under the state lock, and the end takes its relays under it too, so a command that came first wins
        // (see SimulatedPulses).
        Change(() =>
        {
            Pulses.Cancel(bit);
            _relays |= bit;
            Pulses.Start(bit, SmartDenApi.PulseUnit * value, take => Change(() => _relays &= ~take()));
        });
        return Answer(request, pdu.ToArray(), SmartDenTraceText.Confirmed(request.Frame(pdu)), routine: false);
    }

    // FC 0F: the function, address, quantity, byte count and a bit per relay; answers the address and quantity.
    private byte[] WriteCoils(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        var bytes = (count + 7) / 8;
        if (count < 1 || pdu[5] != bytes || pdu.Length != 6 + bytes)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        if (address + count > SmartDenApi.RelayCount)
        {
            return Refuse(request, SmartDenApi.IllegalDataAddress);
        }

        var mask = ChannelMask.FirstN(count).Bits << address;
        if ((FailRelays & mask) != 0)
        {
            return Refuse(request, SmartDenApi.DeviceFailure);
        }

        ulong values = 0;
        for (var i = 0; i < bytes; i++)
        {
            values |= (ulong)pdu[6 + i] << (8 * i);
        }

        values = (values << address) & mask;
        Change(() =>
        {
            Pulses.Cancel(mask);
            _relays = (_relays & ~mask) | values;
        });
        return Answer(request, pdu[..5].ToArray(), "Relays set", routine: false);
    }

    // FC 03: the function, address and quantity, inside one block (section 10.2.1.3: code 2 otherwise).
    private byte[] ReadRegisters(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        if (RefuseScheduleReads)
        {
            return Refuse(request, SmartDenApi.IllegalFunction);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count < 1 || count > SmartDenApi.MaxReadRegisters || BlockOf(address, count) is null)
        {
            return Refuse(request, SmartDenApi.IllegalDataAddress);
        }

        var answer = new byte[2 + (2 * count)];
        answer[0] = SmartDenApi.ReadHoldingRegisters;
        answer[1] = (byte)(2 * count);
        lock (_lock)
        {
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2 + (2 * i)), _registers[(ushort)(address + i)]);
            }
        }

        return Answer(request, answer, string.Create(CultureInfo.InvariantCulture, $"{count} registers from {address:X4}"), routine: true);
    }

    // FC 10: the function, address, quantity, byte count and two bytes per register, inside one block; answers the
    // address and quantity (table 10.28).
    private byte[] WriteRegisters(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6)
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count < 1 || pdu[5] != 2 * count || pdu.Length != 6 + (2 * count))
        {
            return Refuse(request, SmartDenApi.IllegalDataValue);
        }

        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + (2 * i))..]);
        }

        return WriteScheduleRegisters(request, pdu, address, values, echo: pdu[..5].ToArray());
    }

    // Writes values from address, all or none: code 2 outside one block, code 3 for a value out of its range (table
    // 10.7), code 1 while RefuseScheduleWrites.
    private byte[] WriteScheduleRegisters(Request request, ReadOnlySpan<byte> pdu, ushort address, ushort[] values, byte[] echo)
    {
        if (BlockOf(address, values.Length) is not { } block)
        {
            return Refuse(request, SmartDenApi.IllegalDataAddress);
        }

        if (RefuseScheduleWrites)
        {
            return Refuse(request, SmartDenApi.IllegalFunction);
        }

        for (var i = 0; i < values.Length; i++)
        {
            var (_, min, max) = block[address - block[0].Start + i];
            if (values[i] < min || values[i] > max)
            {
                return Refuse(request, SmartDenApi.IllegalDataValue);
            }
        }

        ChangeSchedule(() =>
        {
            for (var i = 0; i < values.Length; i++)
            {
                _registers[(ushort)(address + i)] = values[i];
            }
        });
        var frame = request.Frame(pdu);
        var text = request.Function == SmartDenApi.WriteSingleRegister
            ? SmartDenTraceText.Confirmed(frame)
            : string.Create(CultureInfo.InvariantCulture, $"{values.Length} registers from {address:X4} written");
        return Answer(request, echo, text, routine: false);
    }

    // The block that holds count registers from address, each register's range; null when they aren't all in one.
    private static (ushort Start, ushort Min, ushort Max)[]? BlockOf(ushort address, int count)
    {
        foreach (var block in BlockRanges)
        {
            var start = block[0].Start;
            if (address >= start && address + count <= start + block.Length)
            {
                return block;
            }
        }

        return null;
    }

    private static (ushort Start, ushort Min, ushort Max)[] Block(ushort start, ushort min, ushort max)
    {
        return [.. Enumerable.Range(0, SmartDenApi.ScheduleRows).Select(i => (i == 0 ? start : (ushort)0, min, max))];
    }

    private static Dictionary<ushort, ushort> NewRegisters()
    {
        Dictionary<ushort, ushort> registers = new()
        {
            [SmartDenApi.ScheduleStartDateRegister] = 10,
            [SmartDenApi.ScheduleStartDateRegister + 1] = 9,
            [SmartDenApi.ScheduleStartDateRegister + 2] = 2020,
        };
        foreach (var block in BlockRanges.Skip(1))
        {
            for (var i = 0; i < block.Length; i++)
            {
                registers[(ushort)(block[0].Start + i)] = 0;
            }
        }

        return registers;
    }

    // A row as its registers hold it; under _lock.
    private SmartDenSimulatedRow RowAt(int row)
    {
        var n = (ushort)row;
        return new SmartDenSimulatedRow(
            row,
            _registers[(ushort)(SmartDenApi.RowEnableRegister + n)] == SmartDenApi.RowEnabled,
            _registers[(ushort)(SmartDenApi.RowOutputsRegister + n)],
            _registers[(ushort)(SmartDenApi.RowStateRegister + n)] == 1,
            _registers[(ushort)(SmartDenApi.RowHourRegister + n)],
            _registers[(ushort)(SmartDenApi.RowMinuteRegister + n)],
            _registers[(ushort)(SmartDenApi.RowWeekDaysRegister + n)]);
    }

    // A change of the schedule: nothing without power, as the relays; the page is told.
    private void ChangeSchedule(Action change)
    {
        bool changed;
        lock (_lock)
        {
            if (IsPoweredOff)
            {
                return;
            }

            var before = _registers.ToDictionary();
            change();
            changed = _registers.Any(r => before[r.Key] != r.Value);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private byte[] Refuse(Request request, byte code)
    {
        return Answer(request, [(byte)(request.Function | SmartDenApi.ExceptionFlag), code], SmartDenTraceText.Refused(code), routine: false, isError: true);
    }

    private byte[] Answer(Request request, byte[] pdu, string text, bool routine, bool isError = false)
    {
        var frame = SmartDenModbus.Frame(request.TransactionId, request.Unit, pdu);
        Trace(BoardTraceDirection.Received, text, SmartDenModbus.Hex(frame), isError, routine);
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

    private void Change(Action change, bool power = false)
    {
        bool changed;
        lock (_lock)
        {
            // Without power nothing changes: not a command read before the cut, not a pulse's end. Only the power itself.
            if (IsPoweredOff && !power)
            {
                return;
            }

            var before = _relays;
            change();
            changed = before != _relays;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    // Waits as long as AnswerDelay says before an answer goes out; at once without one.
    private Task DelayAnswerAsync(CancellationToken cancellationToken)
    {
        var delay = AnswerDelay;
        return delay > TimeSpan.Zero ? Task.Delay(delay, TimeProvider, cancellationToken) : Task.CompletedTask;
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

    // What an answer needs from its request's header.
    private readonly record struct Request(ushort TransactionId, byte Unit, byte Function)
    {
        // The request as a whole frame again, for the trace's words.
        public byte[] Frame(ReadOnlySpan<byte> pdu)
        {
            return SmartDenModbus.Frame(TransactionId, Unit, pdu);
        }
    }
}
