using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Advantech;

/// <summary>
/// A fake Advantech ADAM module on a local TCP port, speaking Modbus TCP as the manual says the module does
/// (<see cref="AdamApi"/>): its relays (FC 01, 05, 0F at coil 00017 on) and its digital inputs (FC 02 at 00001 on). A
/// request with a bad address, quantity, value or length gets a Modbus exception answer; one for another protocol gets
/// none. A frame whose header gives a length no Modbus frame has closes the connection, as nothing after it can be read
/// in step. It has no registers: FC 03, 04, 06 and 10 are refused as "no such address". It takes any number of
/// connections, since the manual names no limit. It never tells the app anything by itself, as the real module. Used
/// by the tests and, in development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class AdamSimulator : ISimulatedBoard, ISimulatedBoardRejectConnections, ISimulatedBoardNoAnswer, ISimulatedBoardFailRelays, ISimulatedBoardPower
{
    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;

    // FC 03, 04, 06 and 10: the manual's register functions. The simulator has no registers.
    private const byte ReadHoldingRegisters = 0x03;
    private const byte ReadInputRegisters = 0x04;
    private const byte WriteSingleRegister = 0x06;
    private const byte WriteMultipleRegisters = 0x10;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, string> _connections = new();
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly Lock _lock = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ulong _relays;
    private int _poweredOff;
    private ulong _inputs;
    private ulong? _lastRelaysSent;
    private ulong? _lastInputsSent;
    private int _requestCount;
    private int _unansweredCount;

    public AdamSimulator(BoardModel model)
        : this(model, NullLogger.Instance)
    {
    }

    public AdamSimulator(BoardModel model, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        if (!AdamModels.IsAdam(model))
        {
            throw new ArgumentException($"{model.Name} isn't an ADAM module.", nameof(model));
        }

        Model = model;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model { get; }

    /// <summary>The clock for the traffic's times.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public int Port { get; private set; }

    /// <summary>Never: Modbus has no login.</summary>
    public bool HasLogin => false;

    /// <summary>Its relays and inputs, and the open connections.</summary>
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

    /// <summary>Requests answered since start, exception answers included.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Requests left without an answer: another protocol, or <see cref="NoAnswer"/>.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>How many connections are open.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>When true, new connections are closed at once, as if the module were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>When true, requests are read but never answered, as if the module hung.</summary>
    public bool NoAnswer { get; set; }

    /// <summary>
    /// Relays (bit 0 = relay 1) whose FC 05 and FC 0F are answered with exception 04 and not carried out, for the
    /// tests of a refused command.
    /// </summary>
    public ulong FailRelays { get; set; }

    /// <summary>The requests received, as hex, e.g. "00 01 00 00 00 06 01 05 00 10 FF 00". The newest 1,000.</summary>
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
        Change(() => _relays = turnOn ? _relays | bit : _relays & ~bit);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets digital input <paramref name="input"/>: <paramref name="closed"/> is a DI value of 1, as the app shows it
    /// on. On a dry contact the module reads a closed contact as 0 (see README.md); the simulator has no wiring.
    /// </summary>
    public Task SetInputAsync(int input, bool closed)
    {
        if (Model.Capabilities.InputCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input, $"The {Model.Name} has no inputs.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Model.Capabilities.InputCount);
        var bit = 1UL << (input - 1);
        Change(() => _inputs = closed ? _inputs | bit : _inputs & ~bit);
        return Task.CompletedTask;
    }

    /// <summary>Whether the power is cut: nothing answers, and every relay is off.</summary>
    public bool IsPoweredOff => Volatile.Read(ref _poweredOff) != 0;

    public string AfterPowerCut => "Every relay comes back off.";

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
            _relays = 0UL;
        });
        return Task.CompletedTask;
    }

    /// <summary>Gives the power back: the relays as the board sets them after a power cut (<see cref="AfterPowerCut"/>), and it answers again.</summary>
    public Task RestorePowerAsync()
    {
        if (!IsPoweredOff)
        {
            return Task.CompletedTask;
        }

        Change(() => _relays = 0UL);
        Volatile.Write(ref _poweredOff, 0);
        return Task.CompletedTask;
    }

    public void DropConnections()
    {
        foreach (var connection in _connections.Keys)
        {
            Remove(connection);
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
                var remote = RemoteOf(tcp);
                _connections[tcp] = remote;
                RaiseChanged();
                Trace(BoardTraceDirection.None, $"The app connected from {remote}", null);
                handlers.Add(HandleConnectionAsync(tcp, remote, cancellationToken));
            }
#pragma warning disable CA1031 // One connection that can't be taken must not stop the module taking the next.
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
            // The client went away, its connection was dropped, or the simulator is stopping.
        }
#pragma warning disable CA1031 // Whatever one connection sends must not take down the simulated module or its other work.
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
        var head = new byte[AdamApi.HeaderBytes];
        if (!await ReadAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (length is < 2 or > AdamApi.MaxLength)
        {
            Remember(AdamModbus.Hex(head));
            Trace(
                BoardTraceDirection.Sent,
                string.Create(CultureInfo.InvariantCulture, $"Closed the connection: a frame of length {length}"),
                AdamModbus.Hex(head),
                isError: true);
            return null;
        }

        var frame = new byte[AdamApi.HeaderBytes - 1 + length];
        head.CopyTo(frame, 0);
        return await ReadAsync(stream, frame.AsMemory(AdamApi.HeaderBytes), cancellationToken).ConfigureAwait(false) ? frame : null;
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
        var hex = AdamModbus.Hex(frame);
        Remember(hex);
        var pdu = frame.AsSpan(AdamApi.HeaderBytes);
        var function = pdu[0];
        Trace(BoardTraceDirection.Sent, AdamTraceText.Request(frame), hex, isRoutine: function is AdamApi.ReadCoils or AdamApi.ReadDiscreteInputs);
        var protocol = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        if (protocol != AdamApi.ProtocolId)
        {
            return Silent(string.Create(CultureInfo.InvariantCulture, $"No answer: protocol {protocol} isn't Modbus"));
        }

        if (NoAnswer)
        {
            return Silent("No answer: the module hangs");
        }

        var request = new Request(BinaryPrimitives.ReadUInt16BigEndian(frame), frame[6], function);
        return function switch
        {
            AdamApi.ReadCoils => ReadRelays(request, pdu),
            AdamApi.ReadDiscreteInputs => ReadInputs(request, pdu),
            AdamApi.WriteSingleCoil => WriteCoil(request, pdu),
            AdamApi.WriteMultipleCoils => WriteCoils(request, pdu),
            ReadHoldingRegisters or ReadInputRegisters or WriteSingleRegister or WriteMultipleRegisters => Refuse(request, AdamApi.IllegalDataAddress),
            _ => Refuse(request, AdamApi.IllegalFunction),
        };
    }

    // FC 01: the function, address and quantity; the relays only, from coil 00017.
    private byte[] ReadRelays(Request request, ReadOnlySpan<byte> pdu)
    {
        return ReadBits(request, pdu, AdamApi.RelayAddress, Model.Capabilities.RelayCount, inputs: false);
    }

    // FC 02: the function, address and quantity; the inputs only, from 00001.
    private byte[] ReadInputs(Request request, ReadOnlySpan<byte> pdu)
    {
        return ReadBits(request, pdu, AdamApi.InputAddress, Model.Capabilities.InputCount, inputs: true);
    }

    private byte[] ReadBits(Request request, ReadOnlySpan<byte> pdu, ushort first, int have, bool inputs)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count is < 1 or > AdamModbus.MaxReadBits)
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        if (address < first || address + count > first + have)
        {
            return Refuse(request, AdamApi.IllegalDataAddress);
        }

        ulong bits;
        bool routine;
        lock (_lock)
        {
            bits = ((inputs ? _inputs : _relays) >> (address - first)) & ChannelMask.FirstN(count).Bits;
            ref var lastSent = ref inputs ? ref _lastInputsSent : ref _lastRelaysSent;
            routine = lastSent == bits;
            lastSent = bits;
        }

        var bytes = (count + 7) / 8;
        var answer = new byte[2 + bytes];
        answer[0] = request.Function;
        answer[1] = (byte)bytes;
        for (var i = 0; i < bytes; i++)
        {
            answer[2 + i] = (byte)(bits >> (8 * i));
        }

        var text = inputs ? AdamTraceText.Inputs(bits, count) : AdamTraceText.Relays(bits, count);
        return Answer(request, answer, text, routine);
    }

    // FC 05: FF00 on, 0000 off; the answer is the request, echoed.
    private byte[] WriteCoil(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (address < AdamApi.RelayAddress || address >= AdamApi.RelayAddress + Model.Capabilities.RelayCount)
        {
            return Refuse(request, AdamApi.IllegalDataAddress);
        }

        // The manual also names FF FF, "the forced value to be released"; it doesn't say what that does to a relay.
        if (value is not (AdamApi.CoilOn or AdamApi.CoilOff))
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        var bit = 1UL << (address - AdamApi.RelayAddress);
        if ((FailRelays & bit) != 0)
        {
            return Refuse(request, AdamApi.DeviceFailure);
        }

        Change(() => _relays = value == AdamApi.CoilOn ? _relays | bit : _relays & ~bit);
        return Answer(request, pdu.ToArray(), AdamTraceText.Confirmed(request.Frame(pdu)), routine: false);
    }

    // FC 0F: the function, address, quantity, byte count and a bit per relay; answers the address and quantity.
    private byte[] WriteCoils(Request request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6)
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        var bytes = (count + 7) / 8;
        if (count < 1 || pdu[5] != bytes || pdu.Length != 6 + bytes)
        {
            return Refuse(request, AdamApi.IllegalDataValue);
        }

        if (address < AdamApi.RelayAddress || address + count > AdamApi.RelayAddress + Model.Capabilities.RelayCount)
        {
            return Refuse(request, AdamApi.IllegalDataAddress);
        }

        var shift = address - AdamApi.RelayAddress;
        var mask = ChannelMask.FirstN(count).Bits << shift;
        if ((FailRelays & mask) != 0)
        {
            return Refuse(request, AdamApi.DeviceFailure);
        }

        ulong values = 0;
        for (var i = 0; i < bytes; i++)
        {
            values |= (ulong)pdu[6 + i] << (8 * i);
        }

        values = (values << shift) & mask;
        Change(() => _relays = (_relays & ~mask) | values);
        return Answer(request, pdu[..5].ToArray(), "Relays set", routine: false);
    }

    private byte[] Refuse(Request request, byte code)
    {
        return Answer(request, [(byte)(request.Function | AdamApi.ExceptionFlag), code], AdamTraceText.Refused(code), routine: false, isError: true);
    }

    private byte[] Answer(Request request, byte[] pdu, string text, bool routine, bool isError = false)
    {
        var frame = AdamModbus.Frame(request.TransactionId, request.Unit, pdu);
        Trace(BoardTraceDirection.Received, text, AdamModbus.Hex(frame), isError, routine);
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
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated module.
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
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated module.
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
            return AdamModbus.Frame(TransactionId, Unit, pdu);
        }
    }
}
