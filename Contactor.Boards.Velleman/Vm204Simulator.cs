using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Velleman;

/// <summary>
/// A fake VM204 on a local TCP port: a small HTTP/1.1 server with the board's API routes (<see cref="Vm204Api"/>), as
/// the firmware answers them: the key from a login, 400 for a wrong login or key, the status JSON, and one request
/// per connection. It never tells the app anything by itself, as the real board. Used by the tests and, in
/// development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class Vm204Simulator : ISimulatedBoard, ISimulatedBoardRejectConnections, ISimulatedBoardPower, ISimulatedBoardSlowAnswers, ISimulatedBoardLogin, ISimulatedBoardPulses
{
    /// <summary>How long a client may take to send its whole request head before the connection is closed.</summary>
    public static readonly TimeSpan RequestHeadTimeout = TimeSpan.FromSeconds(5);

    // A request head longer than this isn't one the app sends.
    private const int MaxRequestHeadBytes = 8192;

    // HTTP_SERVER_URI_MAX_LEN and HTTP_SERVER_QUERY_STRING_MAX_LEN (cyclone_tcp/http/http_server.h): longer is a 400.
    private const int MaxPathOrQueryLength = 255;

    // Targets kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedTargetsKept = 1000;

    // The app shows as connected while it asked something this recently: it opens a connection for every request.
    private static readonly TimeSpan ClientWindow = TimeSpan.FromSeconds(10);

    // Swapped whole, so a login never meets a user name from one and a password from another.
    private Credentials _credentials;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _receivedTargets = new();
    private readonly Lock _lock = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private byte _relays;
    private int _poweredOff;
    private long _answerDelayTicks;
    private byte _inputs;
    private int _analog;
    private string _key = NewKey();
    private Vm204Status? _lastStatusSent;
    private SimulatedPulses? _pulses;
    private int _deniedLoginCount;
    private int _requestCount;

    /// <summary>A board whose login is empty, so the app's "no login" gets in.</summary>
    public Vm204Simulator()
        : this(null, null, NullLogger.Instance)
    {
    }

    /// <summary>A board with this login, or an empty one if both are null.</summary>
    public Vm204Simulator(string? username, string? password, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if ((username is null) != (password is null))
        {
            throw new ArgumentException("Give both a user name and a password, or neither.", nameof(password));
        }

        _credentials = new Credentials(username ?? "", password ?? "");
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model => Vm204Model.Model;

    /// <summary>The clock for the traffic's times, for who counts as connected and for the end of a pulse.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The relays whose pulse is still running: the <c>pulse</c> flags of the status.</summary>
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

    public bool HasLogin => Volatile.Read(ref _credentials) is { } credentials && (credentials.Username.Length > 0 || credentials.Password.Length > 0);

    /// <summary>
    /// Its relays, inputs and analog input; the clients are the addresses that asked something within the last 10 s.
    /// </summary>
    public SimulatedBoardState State
    {
        get
        {
            var since = TimeProvider.GetUtcNow() - ClientWindow;
            lock (_lock)
            {
                return new SimulatedBoardState(
                    new ChannelMask(_relays),
                    new ChannelMask(_inputs),
                    [.. _clients.Where(c => c.Value >= since).Select(c => c.Key).Order(StringComparer.Ordinal)])
                {
                    Analog = [_analog],
                };
            }
        }
    }

    public byte Relays
    {
        get
        {
            lock (_lock)
            {
                return _relays;
            }
        }
    }

    /// <summary>The key a login gets.</summary>
    public string Key
    {
        get
        {
            lock (_lock)
            {
                return _key;
            }
        }
    }

    /// <summary>Logins refused because of a wrong user name or password.</summary>
    public int DeniedLoginCount => Volatile.Read(ref _deniedLoginCount);

    /// <summary>Requests answered since start.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>The request targets received, as they arrived (path and query, not decoded): the newest 1,000.</summary>
    public IReadOnlyList<string> ReceivedTargets => [.. _receivedTargets];

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
        Vm204Log.SimulatorListening(_logger, Port);
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        var mask = (byte)Model.Capabilities.Relay(relay).Bits;
        Change(() => _relays = turnOn ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Vm204Api.InputCount);
        var mask = (byte)(1 << (input - 1));
        Change(() => _inputs = closed ? (byte)(_inputs | mask) : (byte)(_inputs & ~mask));
        return Task.CompletedTask;
    }

    /// <summary>Sets the analog input (input 1, the only one), 0–1023 as the board's converter reads it.</summary>
    public Task SetAnalogAsync(int input, int counts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Vm204Api.AnalogInputCount);
        ArgumentOutOfRangeException.ThrowIfNegative(counts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(counts, Vm204Api.AnalogMaxCounts);
        Change(() => _analog = counts);
        return Task.CompletedTask;
    }

    /// <summary>Makes a new key, as the board's web page does: the app's key stops working.</summary>
    public void RegenerateKey()
    {
        lock (_lock)
        {
            _key = NewKey();
        }
    }

    /// <summary>
    /// Changes the login while the board runs, as on its own web page: from then on the app's login no longer fits. The key
    /// the app already has keeps working until it is made anew. Only on a board with a login, and only to another login it takes.
    /// </summary>
    public void ChangeLogin(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        if (!HasLogin)
        {
            throw new InvalidOperationException("The board has no login to change.");
        }

        if (Vm204Login.TryCreate(username, password, out var error) is null || username.Length == 0)
        {
            throw new ArgumentException(error?.ToString() ?? "The VM204 takes a user name and a password.", nameof(username));
        }

        Volatile.Write(ref _credentials, new Credentials(username, password));
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

    public string AfterPowerCut => "Every relay comes back off (not yet checked on a real VM204).";

    /// <summary>
    /// Cuts the power: connections drop, new ones are closed at once, pulses end (their timers are in the board's memory
    /// only) and every relay goes off.
    /// </summary>
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
            _relays = (byte)0;
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
                _relays = (byte)0;
                Volatile.Write(ref _poweredOff, 0);
            },
            power: true);
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
        _connections.TryAdd(tcp, 0);
        try
        {
            if (RejectConnections || IsPoweredOff)
            {
                return;
            }

            var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            var isNew = !_clients.ContainsKey(remote);
            _clients[remote] = TimeProvider.GetUtcNow();
            if (isNew)
            {
                RaiseChanged();
            }

            var stream = tcp.GetStream();
            string? head;

            // A client that connects and says nothing must not hold a handler until the simulator stops.
            using (var headTimeout = new CancellationTokenSource(RequestHeadTimeout, TimeProvider))
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, headTimeout.Token))
            {
                head = await ReadHeadAsync(stream, reading.Token).ConfigureAwait(false);
            }

            if (head is null)
            {
                return;
            }

            var response = Handle(head);
            Interlocked.Increment(ref _requestCount);
            await DelayAnswerAsync(cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the simulator is stopping.
        }
        finally
        {
            _connections.TryRemove(tcp, out _);
            tcp.Dispose();
        }
    }

    // The request line and headers, up to the empty line; null when the client closed first or sent too much.
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return Encoding.Latin1.GetString(buffer, 0, end);
            }
        }

        return null;
    }

    private byte[] Handle(string head)
    {
        var requestLine = head.Split("\r\n", 2)[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            Trace(BoardTraceDirection.Sent, "A request the board doesn't understand", Vm204TraceText.Redact(requestLine), isError: true);
            return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Refused: badly formed", isError: true);
        }

        var target = parts[1];
        _receivedTargets.Enqueue(target);
        while (_receivedTargets.Count > ReceivedTargetsKept && _receivedTargets.TryDequeue(out _))
        {
        }

        var question = target.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        var isStatus = path.Equals(Vm204Api.StatusPath, StringComparison.OrdinalIgnoreCase);
        Trace(BoardTraceDirection.Sent, Vm204TraceText.Request(target), $"GET {Vm204TraceText.Redact(target)}", isRoutine: isStatus);
        if (path.Length > MaxPathOrQueryLength || query.Length > MaxPathOrQueryLength)
        {
            return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Refused: too long", isError: true);
        }

        // As httpServerUriNotFoundCallback (Main/main.c): anything with "/api" in it is the API, split on "/".
        if (!path.Contains("/api", StringComparison.Ordinal))
        {
            return Respond(HttpStatusCode.NotFound, "The requested page could not be found", "Not found", isError: true);
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.ElementAtOrDefault(1) ?? "";
        var second = segments.ElementAtOrDefault(2) ?? "";
        var parameters = Parameters(query);
        if (first == "relay" && second is "on" or "off")
        {
            if (!HasKey(parameters))
            {
                return RefuseKey();
            }

            var mask = Vm204TraceText.RelayMask(query);
            Change(() => _relays = second == "on" ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
            return RespondStatus();
        }

        if (first == "relay" && second == "pulse")
        {
            if (!HasKey(parameters))
            {
                return RefuseKey();
            }

            Pulse(Vm204TraceText.RelayMask(query), Vm204TraceText.PulseSeconds(query));
            return RespondStatus();
        }

        if (first.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return HasKey(parameters) ? RespondStatus() : RefuseKey();
        }

        if (first.Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            // authUser (Main/api.c): strcmp, so case matters, against the values exactly as they arrived.
            var credentials = Volatile.Read(ref _credentials);
            if (parameters.TryGetValue("user", out var user) && user == credentials.Username
                && parameters.TryGetValue("password", out var password) && password == credentials.Password)
            {
                var body = Vm204Json.WriteKey(Key);
                return Respond(HttpStatusCode.OK, body, "Logged in", bytes: Vm204TraceText.Answer(200, body, hideBody: true), json: true);
            }

            Interlocked.Increment(ref _deniedLoginCount);
            return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Wrong user name or password", isError: true);
        }

        // Toggle and the names aren't simulated; the app doesn't use them.
        return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Refused: not simulated", isError: true);
    }

    // As startPulse (pulse.c lines 31–55) for each relay: one whose pulse runs is left as it is, neither turned on nor
    // started again; any other goes on, and off again when its time is up (vTimerCallback, Main/main.c lines 459–470),
    // also when /api/relay/on or /off switched it meanwhile, which don't stop the timer. A time outside 1 s to
    // 2,147,483 s, or none, starts nothing: the firmware's would be a period of 0, an overflow of seconds * 1000 or an
    // uninitialised value, which the app never sends.
    private void Pulse(byte mask, int? seconds)
    {
        if (seconds is not ({ } time and >= Vm204Api.PulseShortestSeconds and <= Vm204Api.PulseLongestSeconds))
        {
            return;
        }

        Change(() =>
        {
            var start = (byte)(mask & ~Pulses.Running);
            _relays |= start;

            // The end takes its relays under the gate (see SimulatedPulses).
            Pulses.Start(start, TimeSpan.FromSeconds(time), take => Change(() => _relays &= (byte)~take()));
        });
    }

    // As the firmware's parsers: name=value pairs split on "&", names in any case, the last one wins, not decoded.
    private static Dictionary<string, string> Parameters(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                parameters[pair[..equals]] = pair[(equals + 1)..];
            }
        }

        return parameters;
    }

    // strcasecmp in the firmware: the key in any case.
    private bool HasKey(Dictionary<string, string> parameters)
    {
        return parameters.TryGetValue("key", out var key) && key.Equals(Key, StringComparison.OrdinalIgnoreCase);
    }

    private byte[] RefuseKey()
    {
        return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Wrong key", isError: true);
    }

    private byte[] RespondStatus()
    {
        Vm204Status status;
        bool routine;
        lock (_lock)
        {
            status = new Vm204Status(_relays, _inputs, _analog, (byte)Pulses.Running);
            routine = status.SameSwitches(_lastStatusSent);
            _lastStatusSent = status;
        }

        var body = Vm204Json.WriteStatus(status);
        return Respond(HttpStatusCode.OK, body, Vm204TraceText.Status(status), bytes: Vm204TraceText.Answer(200, body), isRoutine: routine, json: true);
    }

    private byte[] Respond(
        HttpStatusCode statusCode,
        string body,
        string text,
        string? bytes = null,
        bool isError = false,
        bool isRoutine = false,
        bool json = false)
    {
        Trace(BoardTraceDirection.Received, text, bytes ?? Vm204TraceText.Answer((int)statusCode, body), isError, isRoutine);
        var content = Encoding.ASCII.GetBytes(body);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {(int)statusCode} {Reason(statusCode)}\r\nContent-Type: {(json ? "application/json" : "text/plain")}\r\nContent-Length: {content.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(header), .. content];
    }

    private static string Reason(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            _ => "Not Found",
        };
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

            var before = (_relays, _inputs, _analog, Pulses.Running);
            change();
            changed = before != (_relays, _inputs, _analog, Pulses.Running);
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
                Vm204Log.SubscriberFailed(_logger, ex);
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
                Vm204Log.SubscriberFailed(_logger, ex);
            }
        }
    }

    // 32 letters and digits, as gen_random (Main/main.c).
    private static string NewKey()
    {
        const string alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        return RandomNumberGenerator.GetString(alphanumeric, 32);
    }

    // A class rather than a record, so the password can't leak through a generated ToString.
    private sealed class Credentials
    {
        public Credentials(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public string Username { get; }

        public string Password { get; }
    }
}
