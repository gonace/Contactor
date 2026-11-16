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
/// closes the connection, as nothing after it can be read in step. It has no configuration registers: FC 03 and FC 10
/// are refused as "no such address". It takes one connection at a time, as the manual says the board does: a new one is
/// closed at once while another is open, or, with <see cref="NewConnectionTakesOver"/>, closes the one before (the manual
/// doesn't say which; see README.md). It never tells the app anything by itself, as the real board. Used by the tests and, in
/// development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class SmartDenSimulator : ISimulatedBoard
{
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
    private ulong? _lastRelaysSent;
    private int _requestCount;
    private int _unansweredCount;

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
            if (RejectConnections)
            {
                tcp.Dispose();
                continue;
            }

            try
            {
                if (Admit(tcp) is { } remote)
                {
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
            SmartDenApi.ReadHoldingRegisters or SmartDenApi.WriteMultipleRegisters => Refuse(request, SmartDenApi.IllegalDataAddress),
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

    // FC 06: only the pulse registers. The relay on now, off again after value × 100 ms; echo. A later FC 05 for the
    // relay ends the pulse, a later pulse starts it again.
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
            return Refuse(request, SmartDenApi.IllegalDataAddress);
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

    private void Change(Action change)
    {
        bool changed;
        lock (_lock)
        {
            var before = _relays;
            change();
            changed = before != _relays;
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
