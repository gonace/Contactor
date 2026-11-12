using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Vm204;

/// <summary>
/// A fake VM204 on a local TCP port: a small HTTP/1.1 server with the board's API routes (<see cref="Vm204Api"/>), as
/// the firmware answers them: the key from a login, 400 for a wrong login or key, the status JSON, and one request
/// per connection. It never tells the app anything by itself, as the real board. Used by the tests and, in
/// development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class Vm204Simulator : ISimulatedBoard
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

    private readonly string _username;
    private readonly string _password;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _receivedTargets = new();
    private readonly Lock _lock = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private byte _relays;
    private byte _inputs;
    private int _analog;
    private string _key = NewKey();
    private Vm204Status? _lastStatusSent;
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

        _username = username ?? "";
        _password = password ?? "";
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model => Vm204Model.Model;

    /// <summary>The clock for the traffic's times and for who counts as connected.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public int Port { get; private set; }

    public bool HasLogin => _username.Length > 0 || _password.Length > 0;

    /// <summary>Its relays and inputs; the clients are the addresses that asked something within the last 10 s.</summary>
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
                    [.. _clients.Where(c => c.Value >= since).Select(c => c.Key).Order(StringComparer.Ordinal)]);
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
        _logger.SimulatorListening(Port);
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

    /// <summary>Sets the analog input, 0–1023 as the board's converter reads it.</summary>
    public void SetAnalog(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1023);
        lock (_lock)
        {
            _analog = value;
        }
    }

    /// <summary>Makes a new key, as the board's web page does: the app's key stops working.</summary>
    public void RegenerateKey()
    {
        lock (_lock)
        {
            _key = NewKey();
        }
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
            if (RejectConnections)
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

        if (first.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return HasKey(parameters) ? RespondStatus() : RefuseKey();
        }

        if (first.Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            // authUser (Main/api.c): strcmp, so case matters, against the values exactly as they arrived.
            if (parameters.TryGetValue("user", out var user) && user == _username
                && parameters.TryGetValue("password", out var password) && password == _password)
            {
                var body = Vm204Json.WriteKey(Key);
                return Respond(HttpStatusCode.OK, body, "Logged in", bytes: Vm204TraceText.Answer(200, body, hideBody: true), json: true);
            }

            Interlocked.Increment(ref _deniedLoginCount);
            return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Wrong user name or password", isError: true);
        }

        // Toggle, pulse and the names aren't simulated; the app doesn't use them.
        return Respond(HttpStatusCode.BadRequest, "The request is badly formed", "Refused: not simulated", isError: true);
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
            status = new Vm204Status(_relays, _inputs, _analog);
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

    // 32 letters and digits, as gen_random (Main/main.c).
    private static string NewKey()
    {
        const string alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        return RandomNumberGenerator.GetString(alphanumeric, 32);
    }
}
