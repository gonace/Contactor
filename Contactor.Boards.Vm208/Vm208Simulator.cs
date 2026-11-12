using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Vm208;

/// <summary>
/// A fake VM208 on a local TCP port: a small HTTP/1.1 server with the routes the app uses (<see cref="Vm208Api"/>), as
/// the firmware answers them: the status of the card itself, one relay switched per <c>POST /relay</c>, no login on
/// either, and one request per connection. A request the firmware would never answer (a status without exactly two
/// parameters) or would crash on (a relay request without its four parameters, another interface, a relay outside
/// 1–4) is closed without an answer. It never tells the app anything by itself, as the real board. Used by the tests
/// and, in development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class Vm208Simulator : ISimulatedBoard
{
    // A request head or form longer than this isn't one the app sends.
    private const int MaxRequestHeadBytes = 8192;
    private const int MaxFormBytes = 1024;

    // Requests kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedRequestsKept = 1000;

    // The app shows as connected while it asked something this recently: it opens a connection for every request.
    private static readonly TimeSpan ClientWindow = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _receivedRequests = new();
    private readonly Lock _lock = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private byte _relays;
    private bool _input;
    private Vm208Status? _lastStatusSent;
    private int _requestCount;
    private int _unansweredCount;
    private int _authorizationCount;

    public Vm208Simulator()
        : this(NullLogger.Instance)
    {
    }

    public Vm208Simulator(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model => Vm208Model.Model;

    /// <summary>The clock for the traffic's times and for who counts as connected.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public int Port { get; private set; }

    /// <summary>Never: the board's API takes no login.</summary>
    public bool HasLogin => false;

    /// <summary>Its relays and input; the clients are the addresses that asked something within the last 10 s.</summary>
    public SimulatedBoardState State
    {
        get
        {
            var since = TimeProvider.GetUtcNow() - ClientWindow;
            lock (_lock)
            {
                return new SimulatedBoardState(
                    new ChannelMask(_relays),
                    _input ? ChannelMask.Of(1) : ChannelMask.None,
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

    /// <summary>Requests answered since start.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Requests closed without an answer, as the firmware would leave them or crash on them.</summary>
    public int UnansweredCount => Volatile.Read(ref _unansweredCount);

    /// <summary>Requests that came with an <c>Authorization</c> header.</summary>
    public int AuthorizationCount => Volatile.Read(ref _authorizationCount);

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>
    /// The requests received, as they arrived: "GET /status?interface=-1&amp;socket=0" or
    /// "POST /relay index=1&amp;state=1&amp;interface=-1&amp;socket=0". The newest 1,000.
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
        var mask = (byte)Model.Capabilities.Relay(relay).Bits;
        Change(() => _relays = turnOn ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Vm208Api.InputCount);
        Change(() => _input = closed);
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
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var response = Handle(request.Value.Head, request.Value.Form);
            if (response is null)
            {
                Interlocked.Increment(ref _unansweredCount);
                return;
            }

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

    // The request line and headers, and the form body its Content-Length gives; null when the client closed first or
    // sent too much.
    private static async Task<(string Head, string Form)?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes + MaxFormBytes];
        var length = 0;
        int end;
        while (true)
        {
            end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                break;
            }

            if (length >= MaxRequestHeadBytes)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length, MaxRequestHeadBytes - length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        var head = Encoding.Latin1.GetString(buffer, 0, end);
        var contentLength = 0;
        foreach (var line in head.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
            {
                return null;
            }
        }

        if (contentLength > MaxFormBytes)
        {
            return null;
        }

        var bodyStart = end + 4;
        while (length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, bodyStart + contentLength - length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        return (head, Encoding.Latin1.GetString(buffer, bodyStart, contentLength));
    }

    // The answer, or null to close without one.
    private byte[]? Handle(string head, string form)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            Trace(BoardTraceDirection.Sent, "A request the board doesn't understand", Vm208TraceText.RequestBytes("?", lines[0]), isError: true);
            return Respond(HttpStatusCode.BadRequest, "", "Refused: badly formed", isError: true);
        }

        if (lines.Skip(1).Any(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Increment(ref _authorizationCount);
        }

        var (method, target) = (parts[0], parts[1]);
        var question = target.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        var isPost = method == "POST";
        var shownForm = isPost ? form : null;
        Remember(Vm208TraceText.RequestBytes(method, target, shownForm));
        var isStatus = method == "GET" && path == Vm208Api.StatusPath;
        Trace(BoardTraceDirection.Sent, Vm208TraceText.Request(method, path, shownForm), Vm208TraceText.RequestBytes(method, target, shownForm), isRoutine: isStatus);

        // server.on("/status", HTTP_GET) in src/Network/server_VM208.cpp: exactly two parameters, read by position.
        if (isStatus)
        {
            var values = query.Length == 0 ? [] : query.Split('&');
            if (values.Length != 2)
            {
                return NoAnswer("No answer: the board answers a status only with two parameters");
            }

            return Value(values[0]) == Vm208Api.BaseInterface ? RespondStatus() : NoAnswer("No answer: only the card itself is simulated");
        }

        // server.on("/relay", HTTP_POST): four parameters by position; a missing one or a relay outside 1–4 crashes it.
        if (isPost && path == Vm208Api.RelayPath)
        {
            if (Vm208TraceText.ReadRelayForm(form) is not { } command)
            {
                return NoAnswer("No answer: the board would crash on this request");
            }

            var mask = (byte)(1 << (command.Relay - 1));
            Change(() => _relays = command.TurnOn ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
            return RespondStatus();
        }

        // The board's own pages, behind HTTP basic authentication (serveStatic(...).setAuthentication).
        if (method == "GET" && (path == "/" || path.EndsWith(".html", StringComparison.Ordinal)))
        {
            return Respond(HttpStatusCode.Unauthorized, "", "Asks for the web page's login", isError: true, extraHeader: "WWW-Authenticate: Basic realm=\"Login Required\"\r\n");
        }

        // Pulses, timers, schedules and settings aren't simulated; the app doesn't use them.
        return Respond(HttpStatusCode.NotFound, "", "Not found", isError: true);
    }

    private static string Value(string pair)
    {
        var equals = pair.IndexOf('=', StringComparison.Ordinal);
        return Uri.UnescapeDataString(equals < 0 ? "" : pair[(equals + 1)..]);
    }

    private void Remember(string request)
    {
        _receivedRequests.Enqueue(request);
        while (_receivedRequests.Count > ReceivedRequestsKept && _receivedRequests.TryDequeue(out _))
        {
        }
    }

    private byte[]? NoAnswer(string text)
    {
        Trace(BoardTraceDirection.None, text, null, isError: true);
        return null;
    }

    private byte[] RespondStatus()
    {
        Vm208Status status;
        bool routine;
        lock (_lock)
        {
            status = new Vm208Status(_relays, _input, 0);
            routine = status.SameSwitches(_lastStatusSent);
            _lastStatusSent = status;
        }

        var body = Vm208Json.WriteStatus(status);
        return Respond(HttpStatusCode.OK, body, Vm208TraceText.Status(status), isRoutine: routine, json: true);
    }

    private byte[] Respond(
        HttpStatusCode statusCode,
        string body,
        string text,
        bool isError = false,
        bool isRoutine = false,
        bool json = false,
        string extraHeader = "")
    {
        Trace(BoardTraceDirection.Received, text, Vm208TraceText.Answer((int)statusCode, body), isError, isRoutine);
        var content = Encoding.UTF8.GetBytes(body);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {(int)statusCode} {Reason(statusCode)}\r\nContent-Type: {(json ? "application/json" : "text/plain")}\r\nContent-Length: {content.Length}\r\n{extraHeader}Connection: close\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(header), .. content];
    }

    private static string Reason(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            _ => "Not Found",
        };
    }

    private void Change(Action change)
    {
        bool changed;
        lock (_lock)
        {
            var before = (_relays, _input);
            change();
            changed = before != (_relays, _input);
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
