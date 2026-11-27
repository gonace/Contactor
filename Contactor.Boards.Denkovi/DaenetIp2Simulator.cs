using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// A fake Denkovi DAEnetIP2 v2 kit on a local UDP port, speaking SNMPv1 as the board does (<see cref="DaenetIp2Api"/>):
/// a GetRequest or SetRequest of one OID of ports P3 and P5 (manual, tables 9.4 and 9.5), each pin and each port's whole
/// byte, and a GetRequest of each analog input of P6 (table 9.6), 0 to 1023 as set with <see cref="SetAnalogAsync"/>;
/// a set of one is answered noSuchName, as RFC 1157 (section 4.1.5) says for a variable that can't be set. A get takes
/// the read-only or the read-write community, a set only the read-write one, as section 9 says; a wrong community gets
/// no answer, as SNMPv1 (RFC 1157) drops a message that isn't authentic. Any other OID gets the error-status
/// noSuchName, a value out of range badValue. Only GetRequest and SetRequest are answered (section 9:
/// snmpwalk isn't supported). A datagram it can't read is skipped, and the next one served. Its relays are the kit's
/// pins: a pin that is High is a relay that is on. It never tells the app anything by itself, as the real board. It
/// holds the firmware version (<c>cfgVersion</c>, <see cref="FirmwareVersion"/>) and, from firmware 4.097, the ICMP
/// monitoring's restart pins (<c>cfgP5DupRST</c>, <see cref="RestartPins"/>), which a get and a set reach (table 9.1);
/// it doesn't pulse them. It records every request for an OID whose read changes the real board (<c>cfgReset</c>,
/// <c>cfgDefault</c>, <c>pctrlRestart</c>, <see cref="DaenetIp2Oid.Forbidden"/>) in <see cref="ForbiddenRequests"/>, so
/// a test fails on one, and answers it nothing. Used by the tests and, in development, by Contactor.Simulator. It
/// listens on the loopback interface only. Its traces show the PDU only, never a community.
/// </summary>
public sealed class DaenetIp2Simulator : ISimulatedBoard, ISimulatedBoardNoAnswer, ISimulatedBoardFailRelays, ISimulatedBoardPower, ISimulatedBoardSlowAnswers, ISimulatedBoardLogin
{
    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;

    // Where the app was heard from, kept for the page; a long-running simulator keeps only the newest.
    private const int ClientsKept = 16;

    private readonly ILogger _logger;
    private readonly DaenetIp2Layout _layout;
    private volatile string _community;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly ConcurrentQueue<string> _forbiddenRequests = new();
    private readonly Lock _lock = new();
    private readonly List<string> _clients = [];
    private Socket? _socket;
    private Task? _loop;
    private int _p3;
    private int _p5;
    private int _poweredOff;
    private long _answerDelayTicks;
    private ulong _beforeCut;
    private readonly Dictionary<DaenetIp2Port, int> _lastPortSent = [];

    // What each analog input of P6 reads, input 1 first. The last values sent only mark the answers that change nothing
    // as routine.
    private readonly int[] _analog = new int[DaenetIp2Api.AnalogInputs];
    private readonly int?[] _lastAnalogSent = new int?[DaenetIp2Api.AnalogInputs];
    private int _requestCount;
    private int _unansweredCount;
    private int _dropRequests;
    private bool _noAnalogAnswer;
    private int _firmwareVersion = DaenetIp2Api.RestartPinsFirmware;
    private int _restartPins;
    private bool _refuseRestartPinWrites;

    /// <summary>A kit with the factory communities.</summary>
    public DaenetIp2Simulator(BoardModel model)
        : this(model, null, NullLogger.Instance)
    {
    }

    /// <param name="model">The kit it pretends to be.</param>
    /// <param name="community">Its read-write community; null or empty for the factory one, "private".</param>
    /// <param name="logger">Where it logs.</param>
    public DaenetIp2Simulator(BoardModel model, string? community, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        _layout = DaenetIp2Models.Layout(model);
        community = string.IsNullOrEmpty(community) ? DaenetIp2Api.FactoryReadWriteCommunity : community;
        if (DaenetIp2Models.CheckCommunity(community) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(community));
        }

        Model = model;
        _community = community;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model { get; }

    /// <summary>The clock for the traffic's times.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The read-only community: a get takes it too. The factory one, "000000000000", unless set.</summary>
    public string ReadOnlyCommunity
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (DaenetIp2Models.CheckCommunity(value) is { } error)
            {
                throw new ArgumentException(error.ToString(), nameof(value));
            }

            field = value;
        }
    } = DaenetIp2Api.FactoryReadOnlyCommunity;

    public int Port { get; private set; }

    /// <summary>Always: every SNMP request carries a community.</summary>
    public bool HasLogin => true;

    /// <summary>
    /// Its relays and analog inputs; it has no digital inputs, and the clients are where the app was heard from.
    /// </summary>
    public SimulatedBoardState State
    {
        get
        {
            lock (_lock)
            {
                return new SimulatedBoardState(new ChannelMask(RelaysUnlocked), ChannelMask.None, [.. _clients.Order(StringComparer.Ordinal)])
                {
                    Analog = [.. _analog],
                };
            }
        }
    }

    /// <summary>The kit's relays that are on, bit 0 = relay 1.</summary>
    public ulong Relays
    {
        get
        {
            lock (_lock)
            {
                return RelaysUnlocked;
            }
        }
    }

    /// <summary>Port P3's byte, bit 0 = P3.1.</summary>
    public int P3
    {
        get
        {
            lock (_lock)
            {
                return _p3;
            }
        }
    }

    /// <summary>Port P5's byte, bit 0 = P5.1.</summary>
    public int P5
    {
        get
        {
            lock (_lock)
            {
                return _p5;
            }
        }
    }

    /// <summary>Requests answered since start, error answers included.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Datagrams left without an answer: unreadable, a wrong community, dropped, or <see cref="NoAnswer"/>.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>When true, requests are read but never answered, as if the board hung.</summary>
    public bool NoAnswer { get; set; }

    /// <summary>How many of the next requests are read but not answered, as if the network lost them; then it answers again.</summary>
    public int DropRequests
    {
        get => Volatile.Read(ref _dropRequests);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _dropRequests, value);
        }
    }

    /// <summary>
    /// When true, a get of an analog input is read but never answered, as a board that doesn't answer P6; the relays
    /// are still answered. For the tests of an analog failure that must not take the relays away.
    /// </summary>
    public bool NoAnalogAnswer
    {
        // Read on the receive thread, set by a test.
        get => Volatile.Read(ref _noAnalogAnswer);
        set => Volatile.Write(ref _noAnalogAnswer, value);
    }

    /// <summary>
    /// Relays (bit 0 = relay 1) whose set is answered with the error-status genErr and not carried out, for the tests
    /// of a refused command.
    /// </summary>
    public ulong FailRelays { get; set; }

    /// <summary>
    /// Relays (bit 0 = relay 1) whose set is read but never answered nor carried out, as if the network lost every try,
    /// for the tests of a mask that goes on after a set without an answer.
    /// </summary>
    public ulong SilentRelays { get; set; }

    /// <summary>The PDU of each request received, as hex, without the version and the community. The newest 1,000.</summary>
    public IReadOnlyList<string> ReceivedRequests => [.. _receivedRequests];

    /// <summary>
    /// The PDU of each request for an OID whose read or write changes the real board (<see cref="DaenetIp2Oid.Forbidden"/>),
    /// whatever its type or community, as hex: the app must never send one, so a test fails on any. Never answered.
    /// </summary>
    public IReadOnlyList<string> ForbiddenRequests => [.. _forbiddenRequests];

    /// <summary>
    /// The firmware version it answers for <c>cfgVersion</c>, "LSB=VER_MINOR, MSB=VER_MAJOR" (table 9.1): 4.097 by default,
    /// the first with <c>cfgP5DupRST</c>. An older one answers <c>cfgP5DupRST</c> with no such name, as an OID it doesn't
    /// have.
    /// </summary>
    public int FirmwareVersion
    {
        get => Volatile.Read(ref _firmwareVersion);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, DaenetIp2Oid.FirmwareVersion.MaxValue);
            Volatile.Write(ref _firmwareVersion, value);
            RaiseChanged();
        }
    }

    /// <summary>Whether its firmware has <c>cfgP5DupRST</c> (4.097 and later).</summary>
    public bool HasRestartPins => FirmwareVersion >= DaenetIp2Api.RestartPinsFirmware;

    /// <summary>The ICMP monitoring's restart pins, <c>cfgP5DupRST</c>: bit 0 = P5.1. None by default.</summary>
    public int RestartPins
    {
        get
        {
            lock (_lock)
            {
                return _restartPins;
            }
        }
    }

    /// <summary>When true, a set of <c>cfgP5DupRST</c> gets the error-status genErr and isn't carried out.</summary>
    public bool RefuseRestartPinWrites
    {
        get => Volatile.Read(ref _refuseRestartPinWrites);
        set => Volatile.Write(ref _refuseRestartPinWrites, value);
    }

    private ulong RelaysUnlocked => _layout.RelaysOf(_p3, _p5);

    public void Start(int port = 0)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("The simulator is already started.");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _loop = ServeAsync(socket, _stopping.Token);
        DaenetIp2Log.SimulatorListening(_logger, Port);
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        Model.Capabilities.Relay(relay);
        var pin = _layout.PinOf(relay);
        Change(() => SetPin(pin, turnOn));
        return Task.CompletedTask;
    }

    /// <summary>Sets what an analog input of P6 reads, 0 to 1023 as the board's 10-bit converter reads it.</summary>
    public Task SetAnalogAsync(int input, int counts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, DaenetIp2Api.AnalogInputs);
        ArgumentOutOfRangeException.ThrowIfNegative(counts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(counts, DaenetIp2Api.AnalogMaxCounts);
        bool changed;
        lock (_lock)
        {
            changed = _analog[input - 1] != counts;
            _analog[input - 1] = counts;
        }

        if (changed)
        {
            RaiseChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the restart pins (<c>cfgP5DupRST</c>, 0 to 255, bit 0 = P5.1) as the board's own web page would ("Port P5
    /// Restart Pins", section 6.10.2), and tells the app's page if anything changed.
    /// </summary>
    public Task SetRestartPinsAsync(int pins)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pins);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pins, DaenetIp2Api.MaxPortValue);
        bool changed;
        lock (_lock)
        {
            changed = _restartPins != pins;
            _restartPins = pins;
        }

        if (changed)
        {
            RaiseChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>Throws: the kits have no digital inputs.</summary>
    public Task SetInputAsync(int input, bool closed)
    {
        throw new ArgumentOutOfRangeException(nameof(input), input, $"The {Model.Name} has no inputs.");
    }

    /// <summary>
    /// Changes the read-write community while the board runs, as on its own SNMP settings: from then on a request with
    /// the app's community gets no answer. <paramref name="username"/> is ignored: the community is the board's login.
    /// </summary>
    public void ChangeLogin(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (DaenetIp2Models.CheckCommunity(password) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(password));
        }

        _community = password;
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

    /// <summary>Whether the power is cut: no request gets an answer, and every relay is off.</summary>
    public bool IsPoweredOff => Volatile.Read(ref _poweredOff) != 0;

    /// <summary>
    /// "Save Outputs" (section 6.3, <c>cfgNewMode</c> bit 0): the board sets the pins again after a restart as they were;
    /// off, they come back off. Off by default here, as the manual gives no factory value.
    /// </summary>
    public bool SaveOutputs { get; set; }

    public string AfterPowerCut => SaveOutputs
        ? "Save Outputs is on: the relays come back as they were."
        : "Save Outputs is off: every relay comes back off.";

    /// <summary>Cuts the power: requests go unanswered and every relay goes off.</summary>
    public Task CutPowerAsync()
    {
        if (Interlocked.Exchange(ref _poweredOff, 1) != 0)
        {
            return Task.CompletedTask;
        }

        DropConnections();
        Change(() =>
        {
            _beforeCut = RelaysUnlocked;
            SetRelaysUnlocked(0);
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
                SetRelaysUnlocked(SaveOutputs ? _beforeCut : 0);
                Volatile.Write(ref _poweredOff, 0);
            },
            power: true);
        return Task.CompletedTask;
    }

    // Under _lock: every relay's pin as in relays (bit 0 = relay 1).
    private void SetRelaysUnlocked(ulong relays)
    {
        for (var relay = 1; relay <= Model.Capabilities.RelayCount; relay++)
        {
            SetPin(_layout.PinOf(relay), (relays & (1UL << (relay - 1))) != 0);
        }
    }

    /// <summary>UDP has no connection to drop: it forgets where the app was heard from.</summary>
    public void DropConnections()
    {
        bool had;
        lock (_lock)
        {
            had = _clients.Count > 0;
            _clients.Clear();
        }

        if (had)
        {
            RaiseChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        // Let Start return at once.
        await Task.Yield();

        // One byte more than any message: a datagram that fills it is too long to be one.
        var buffer = new byte[DaenetIp2Api.MaxMessageBytes + 1];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize && !cancellationToken.IsCancellationRequested)
            {
                // Windows: an earlier answer's client went away, or a datagram too long for the buffer. The next is served.
                Unanswered($"Skipped: {ex.Message}");
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
                break;
            }

            try
            {
                if (Handle(buffer.AsSpan(0, received.ReceivedBytes), received.RemoteEndPoint) is { } answer)
                {
                    Interlocked.Increment(ref _requestCount);
                    if (AnswerDelay > TimeSpan.Zero)
                    {
                        // Off the loop, so one slow answer doesn't hold up the datagrams after it.
                        _ = AnswerLaterAsync(socket, answer, received.RemoteEndPoint, cancellationToken);
                    }
                    else
                    {
                        await socket.SendToAsync(answer, SocketFlags.None, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
#pragma warning disable CA1031 // Whatever one datagram holds, or a failed send, must not take down the simulated board.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                DaenetIp2Log.SimulatorDatagramFailed(_logger, ex);
                Trace(BoardTraceDirection.None, $"Couldn't answer: {ex.Message}", null, isError: true);
            }
        }
    }

    // Sends an answer after AnswerDelay, unless the power was cut meanwhile: then it is lost, as on the board.
    private async Task AnswerLaterAsync(Socket socket, byte[] answer, EndPoint to, CancellationToken cancellationToken)
    {
        try
        {
            await DelayAnswerAsync(cancellationToken).ConfigureAwait(false);
            if (IsPoweredOff)
            {
                Unanswered("No answer: the power went before it");
                return;
            }

            await socket.SendToAsync(answer, SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The simulator is stopping.
        }
#pragma warning disable CA1031 // A failed send must not take down the simulated board.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DaenetIp2Log.SimulatorDatagramFailed(_logger, ex);
            Trace(BoardTraceDirection.None, $"Couldn't answer: {ex.Message}", null, isError: true);
        }
    }

    // The answer to one datagram, or null to send none.
    private byte[]? Handle(ReadOnlySpan<byte> datagram, EndPoint from)
    {
        if (datagram.Length > DaenetIp2Api.MaxMessageBytes)
        {
            return Unanswered(string.Create(CultureInfo.InvariantCulture, $"No answer: a datagram longer than {DaenetIp2Api.MaxMessageBytes} bytes"));
        }

        DaenetIp2Message request;
        try
        {
            request = DaenetIp2Snmp.Decode(datagram);
        }
        catch (DaenetIp2ProtocolException ex)
        {
            // Its bytes may hold a community: only its length shows.
            return Unanswered(string.Create(CultureInfo.InvariantCulture, $"No answer: an unreadable datagram of {datagram.Length} bytes: {ex.Message}"));
        }

        var hex = DaenetIp2Snmp.Hex(request.Pdu);
        Remember(hex);
        var routine = request.PduType == DaenetIp2Api.GetRequest;
        Trace(BoardTraceDirection.Sent, DaenetIp2TraceText.Request(request.PduType, request.Oid, request.Value, _layout), hex, isRoutine: routine);

        // A read of one of these restarts the real board or loads its factory settings: kept for the tests, whatever the
        // rest of the message, and never answered.
        if (DaenetIp2Oid.IsForbidden(request.Oid))
        {
            _forbiddenRequests.Enqueue(hex);
            return Unanswered($"No answer: {DaenetIp2Snmp.OidText(request.Oid)} would restart the real board or load its factory settings");
        }
        if (request.Version != DaenetIp2Api.Version1)
        {
            return Unanswered(string.Create(CultureInfo.InvariantCulture, $"No answer: version field {request.Version} isn't SNMPv1"));
        }

        if (request.PduType is not (DaenetIp2Api.GetRequest or DaenetIp2Api.SetRequest))
        {
            return Unanswered("No answer: only snmpget and snmpset are taken");
        }

        // A get takes either community, a set the read-write one only. The community itself is never traced.
        var readWrite = _community;
        var community = request.HasCommunity(readWrite) ? readWrite
            : request.PduType == DaenetIp2Api.GetRequest && request.HasCommunity(ReadOnlyCommunity) ? ReadOnlyCommunity
            : null;
        if (community is null)
        {
            return Unanswered("No answer: the community is wrong for it");
        }

        if (IsPoweredOff)
        {
            return Unanswered("No answer: the power is cut");
        }

        if (NoAnswer)
        {
            return Unanswered("No answer: the board hangs");
        }

        if (TryDrop())
        {
            return Unanswered("No answer: the request was lost");
        }

        if (DaenetIp2Api.AnalogInputOf(request.Oid) is { } input)
        {
            if (request.PduType == DaenetIp2Api.GetRequest && NoAnalogAnswer)
            {
                return Unanswered("No answer: the board doesn't answer its analog inputs");
            }

            Remember(from);

            // Read-only (table 9.6): SNMPv1 answers a set of it with noSuchName (RFC 1157, section 4.1.5).
            return request.PduType == DaenetIp2Api.GetRequest ? GetAnalog(request, community, input) : Refuse(request, community, DaenetIp2Api.NoSuchName);
        }

        Remember(from);
        if (request.Oid.SequenceEqual(DaenetIp2Oid.FirmwareVersion.Arcs))
        {
            // Read-only (table 9.1): a set gets no such name, as for an analog input.
            var version = FirmwareVersion;
            return request.PduType == DaenetIp2Api.GetRequest
                ? Answer(request, community, version, DaenetIp2TraceText.Firmware(version), routine: true)
                : Refuse(request, community, DaenetIp2Api.NoSuchName);
        }

        if (request.Oid.SequenceEqual(DaenetIp2Oid.RestartPins.Arcs))
        {
            // From firmware 4.097 only (table 9.1): before that, an OID it doesn't have.
            if (!HasRestartPins)
            {
                return Refuse(request, community, DaenetIp2Api.NoSuchName);
            }

            return request.PduType == DaenetIp2Api.GetRequest ? GetRestartPins(request, community) : SetRestartPins(request, community);
        }

        var (port, pin) = DaenetIp2TraceText.Target(request.Oid);
        if (port is not { } p)
        {
            return Refuse(request, community, DaenetIp2Api.NoSuchName);
        }

        return request.PduType == DaenetIp2Api.GetRequest ? Get(request, community, p, pin) : Set(request, community, p, pin);
    }

    // A pin's level, or the port's whole byte.
    private byte[] Get(DaenetIp2Message request, string community, DaenetIp2Port port, DaenetIp2Pin? pin)
    {
        int value;
        bool routine;
        lock (_lock)
        {
            var bits = port == DaenetIp2Port.P3 ? _p3 : _p5;
            value = pin is { } one ? ((bits & one.Bit) != 0 ? DaenetIp2Api.High : DaenetIp2Api.Low) : bits;
            routine = pin is null && _lastPortSent.TryGetValue(port, out var sent) && sent == bits;
            if (pin is null)
            {
                _lastPortSent[port] = bits;
            }
        }

        var text = pin is { } p ? DaenetIp2TraceText.Pin(p, value) : DaenetIp2TraceText.Port(port, value);
        return Answer(request, community, value, text, routine);
    }

    // An analog input's value, 0 to 1023.
    private byte[] GetAnalog(DaenetIp2Message request, string community, int input)
    {
        int counts;
        bool routine;
        lock (_lock)
        {
            counts = _analog[input - 1];
            routine = _lastAnalogSent[input - 1] == counts;
            _lastAnalogSent[input - 1] = counts;
        }

        return Answer(request, community, counts, DaenetIp2TraceText.Analog(input, counts, DaenetIp2Models.Analog), routine);
    }

    // cfgP5DupRST, 0 to 255.
    private byte[] GetRestartPins(DaenetIp2Message request, string community)
    {
        var pins = RestartPins;
        return Answer(request, community, pins, DaenetIp2TraceText.RestartPins(pins, _layout), routine: true);
    }

    // cfgP5DupRST to 0..255; the answer echoes the value. The pins aren't pulsed here.
    private byte[] SetRestartPins(DaenetIp2Message request, string community)
    {
        if (request.ValueType != DaenetIp2Api.IntegerTag || request.Value is not { } value || value < 0 || value > DaenetIp2Api.MaxPortValue)
        {
            return Refuse(request, community, DaenetIp2Api.BadValue);
        }

        if (RefuseRestartPinWrites)
        {
            return Refuse(request, community, DaenetIp2Api.GenErr);
        }

        _ = SetRestartPinsAsync(value);
        return Answer(request, community, value, DaenetIp2TraceText.Confirmed(request.Oid, value, _layout), routine: false);
    }

    // A pin to High(1) or Low(0), or the port's whole byte to 0..255; the answer echoes the value.
    private byte[]? Set(DaenetIp2Message request, string community, DaenetIp2Port port, DaenetIp2Pin? pin)
    {
        var max = pin is null ? DaenetIp2Api.MaxPortValue : DaenetIp2Api.High;
        if (request.ValueType != DaenetIp2Api.IntegerTag || request.Value is not { } value || value < 0 || value > max)
        {
            return Refuse(request, community, DaenetIp2Api.BadValue);
        }

        // The relays the set changes the pins of.
        var pins = pin is { } one ? [one] : Enumerable.Range(1, DaenetIp2Api.PinsPerPort).Select(n => new DaenetIp2Pin(port, n)).ToArray();
        var relays = pins.Select(_layout.RelayOf).OfType<int>().Aggregate(0UL, (mask, relay) => mask | (1UL << (relay - 1)));
        if ((SilentRelays & relays) != 0)
        {
            return Unanswered("No answer: the set was lost");
        }

        if ((FailRelays & relays) != 0)
        {
            return Refuse(request, community, DaenetIp2Api.GenErr);
        }

        Change(() =>
        {
            if (pin is { } single)
            {
                SetPin(single, value == DaenetIp2Api.High);
            }
            else if (port == DaenetIp2Port.P3)
            {
                _p3 = value;
            }
            else
            {
                _p5 = value;
            }
        });
        return Answer(request, community, value, DaenetIp2TraceText.Confirmed(request.Oid, value, _layout), routine: false);
    }

    // Under _lock.
    private void SetPin(DaenetIp2Pin pin, bool high)
    {
        if (pin.Port == DaenetIp2Port.P3)
        {
            _p3 = high ? _p3 | pin.Bit : _p3 & ~pin.Bit;
        }
        else
        {
            _p5 = high ? _p5 | pin.Bit : _p5 & ~pin.Bit;
        }
    }

    private byte[] Refuse(DaenetIp2Message request, string community, int errorStatus)
    {
        var answer = DaenetIp2Snmp.Response(request, community, errorStatus, errorIndex: 1);
        Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Refused(errorStatus), DaenetIp2Snmp.Hex(PduOf(answer)), isError: true);
        return answer;
    }

    private byte[] Answer(DaenetIp2Message request, string community, int value, string text, bool routine)
    {
        var answer = DaenetIp2Snmp.Response(request, community, DaenetIp2Api.NoError, 0, value);
        Trace(BoardTraceDirection.Received, text, DaenetIp2Snmp.Hex(PduOf(answer)), isRoutine: routine);
        return answer;
    }

    // An answer's PDU, for the trace; the answer is ours, so it reads.
    private static ReadOnlySpan<byte> PduOf(byte[] answer)
    {
        return DaenetIp2Snmp.Decode(answer).Pdu;
    }

    /// <summary>Makes one more of the next requests lost, at once against the board counting them down.</summary>
    public void AddDrop()
    {
        Interlocked.Increment(ref _dropRequests);
    }

    private bool TryDrop()
    {
        while (true)
        {
            var left = Volatile.Read(ref _dropRequests);
            if (left <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _dropRequests, left - 1, left) == left)
            {
                return true;
            }
        }
    }

    private byte[]? Unanswered(string text)
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

    private void Remember(EndPoint from)
    {
        var client = from.ToString() ?? "unknown";
        bool added;
        lock (_lock)
        {
            added = !_clients.Contains(client);
            if (added)
            {
                _clients.Add(client);
                if (_clients.Count > ClientsKept)
                {
                    _clients.RemoveAt(0);
                }
            }
        }

        if (added)
        {
            RaiseChanged();
            Trace(BoardTraceDirection.None, $"The app asked from {client}", null);
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

            var before = (_p3, _p5);
            change();
            changed = before != (_p3, _p5);
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
                DaenetIp2Log.SubscriberFailed(_logger, ex);
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
                DaenetIp2Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
