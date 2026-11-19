using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Velleman;

/// <summary>
/// A fake VM208 on a local TCP port: a small HTTP/1.1 server with the routes the app uses (<see cref="Vm208Api"/>), as
/// the firmware answers them: the status of the card itself, one relay switched per <c>POST /relay</c>, no login on
/// either, and one request per connection. A request the firmware would never answer (a status without exactly two
/// parameters) or would crash on (a relay request without its four parameters, another interface, a relay outside
/// 1–4) is closed without an answer. It never tells the app anything by itself, as the real board. It also holds each
/// relay's week schedule (<see cref="Vm208Alarm"/>) and answers <c>GET /getalarms</c> and <c>POST /shedule_set</c> as
/// the firmware does; channel 0 and a channel above 268 crash the real board, and a channel 5–268 is an extension
/// module's, which isn't simulated: no answer for either. It keeps the schedules but doesn't run them. Used by the
/// tests and, in development, by Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class Vm208Simulator : ISimulatedBoard, ISimulatedBoardTimers
{
    // The firmware's schedule table: the card's 4 relays and the extension modules' channels (config_vm208.cpp).
    private const int ScheduleTableSize = 268;

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
    private readonly IReadOnlyList<Vm208Alarm>[] _alarms = [.. Enumerable.Range(0, Vm208Api.RelayCount).Select(_ => FactorySchedule)];
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

    /// <summary>
    /// A relay's schedule as the board ships it (<c>data/alarms/0.json</c> in the firmware): every alarm disabled, "on"
    /// at 13:28 and "off" at 13:29.
    /// </summary>
    public static IReadOnlyList<Vm208Alarm> FactorySchedule { get; } =
        [.. Enumerable.Range(0, Vm208Api.AlarmCount).Select(i => new Vm208Alarm(i % 7, 13, i < 7 ? 28 : 29, i < 7, false))];

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

    /// <summary>The relays whose schedule has an enabled alarm.</summary>
    public ChannelMask Timers
    {
        get
        {
            lock (_lock)
            {
                return ChannelMask.Of(Enumerable.Range(1, Vm208Api.RelayCount).Where(r => _alarms[r - 1].Any(a => a.Enabled)));
            }
        }
    }

    /// <summary>
    /// The relay's schedule (1–4) as it is now. Throws <see cref="ArgumentOutOfRangeException"/> for a relay the board
    /// doesn't have.
    /// </summary>
    public IReadOnlyList<Vm208Alarm> Alarms(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, Vm208Api.RelayCount);
        lock (_lock)
        {
            return _alarms[relay - 1];
        }
    }

    /// <summary>
    /// Sets a relay's whole schedule as the board's own page would. Throws <see cref="ArgumentOutOfRangeException"/> for
    /// a relay the board doesn't have, and <see cref="ArgumentException"/> for a schedule the page can't make.
    /// </summary>
    public Task SetAlarmsAsync(int relay, IReadOnlyList<Vm208Alarm> alarms)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, Vm208Api.RelayCount);
        ArgumentNullException.ThrowIfNull(alarms);
        if (!Vm208Alarm.IsSchedule(alarms))
        {
            throw new ArgumentException("A VM208 schedule is 14 alarms in the firmware's order, each at a time of day.", nameof(alarms));
        }

        IReadOnlyList<Vm208Alarm> copy = [.. alarms];
        ChangeSchedule(() => _alarms[relay - 1] = copy);
        return Task.CompletedTask;
    }

    /// <summary>Enables every alarm of the relays in <paramref name="timers"/> and disables the others' alarms, at their times.</summary>
    public Task SetTimersAsync(ChannelMask timers)
    {
        if (!Model.Capabilities.AllRelays.ContainsAll(timers))
        {
            throw new ArgumentOutOfRangeException(nameof(timers), timers, "The VM208 has relays 1–4 only.");
        }

        ChangeSchedule(() =>
        {
            for (var relay = 1; relay <= Vm208Api.RelayCount; relay++)
            {
                _alarms[relay - 1] = Enabled(_alarms[relay - 1], timers.Contains(relay));
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Enables (or disables) every alarm of one relay's schedule, at their times, as the board's own page would.</summary>
    public Task SetTimerAsync(int relay, bool turnOn)
    {
        // A relay the board doesn't have faults the task instead of throwing at once, as for the VM201.
        if (relay is < 1 or > Vm208Api.RelayCount)
        {
            return Task.FromException(new ArgumentOutOfRangeException(nameof(relay), relay, "The VM208 has relays 1–4 only."));
        }

        ChangeSchedule(() => _alarms[relay - 1] = Enabled(_alarms[relay - 1], turnOn));
        return Task.CompletedTask;
    }

    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>
    /// How many of the next statuses come busy (<c>{"Channels":[]}</c>), as from a board that couldn't take its I/O lock
    /// within 1 s. A relay request still switches the relay, as the firmware does under its own, shorter lock. 0 or less:
    /// none.
    /// </summary>
    public int BusyNext
    {
        get => Volatile.Read(ref _busyNext);
        set => Volatile.Write(ref _busyNext, value);
    }

    private int _busyNext;

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
        Vm208Log.SimulatorListening(_logger, Port);
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

        // ESPAsyncWebServer reads a body's parameters only from a form (AsyncWebServerRequest::_isPlainPost); any
        // other body leaves the request without them.
        if (!IsPlainPost(ContentType(lines.Skip(1)), form))
        {
            form = "";
        }

        var (method, target) = (parts[0], parts[1]);
        var question = target.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        var isPost = method == "POST";
        var shownForm = isPost ? form : null;
        // Whole, not cut as the traffic shows it: a schedule's form is longer than that.
        Remember(shownForm is null ? $"{method} {target}" : $"{method} {target} {shownForm}");
        var isStatus = method == "GET" && path == Vm208Api.StatusPath;
        var isAlarms = method == "GET" && path == Vm208Api.AlarmsPath;
        Trace(BoardTraceDirection.Sent, Vm208TraceText.Request(method, path, shownForm, query), Vm208TraceText.RequestBytes(method, target, shownForm), isRoutine: isStatus || isAlarms);

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

        // server.on("/getalarms", HTTP_GET), lines 231–281: the first parameter by position, unchecked, as a 16-bit number;
        // getShedule(id - 1) is null from index 268 on (channel 0 is 65535), and the handler crashes on it.
        if (isAlarms)
        {
            var values = Vm208TraceText.Values(query);
            if (values.Count == 0)
            {
                return NoAnswer("No answer: the board would crash on a schedule request without its channel");
            }

            var index = (ushort)((ushort)Vm208TraceText.ToInt(values[0]) - 1);
            if (index >= ScheduleTableSize)
            {
                return NoAnswer("No answer: the board would crash on this channel");
            }

            if (index >= Vm208Api.RelayCount)
            {
                return NoAnswer("No answer: only the card itself is simulated");
            }

            IReadOnlyList<Vm208Alarm> alarms;
            lock (_lock)
            {
                alarms = _alarms[index];
            }

            return Respond(HttpStatusCode.OK, Vm208Json.WriteAlarms(alarms), Vm208TraceText.Schedule(index + 1, alarms), isRoutine: true, json: true);
        }

        // server.on("/shedule_set", HTTP_POST), lines 522–572: exactly 29 parameters (query and form together) or 400;
        // the relay as toInt() - 1 into a 16-bit index, unchecked (relay 0 writes far outside the table), then saved.
        if (isPost && path == Vm208Api.ScheduleSetPath)
        {
            var all = string.Join('&', new[] { query, form }.Where(p => p.Length > 0));
            if (Vm208TraceText.ReadScheduleForm(all) is not { } schedule)
            {
                return Respond(HttpStatusCode.BadRequest, "", "Refused: not 29 parameters", isError: true);
            }

            var index = (ushort)(schedule.Relay - 1);
            if (index >= ScheduleTableSize)
            {
                return NoAnswer("No answer: the board would crash on this relay");
            }

            if (index >= Vm208Api.RelayCount)
            {
                return NoAnswer("No answer: only the card itself is simulated");
            }

            ChangeSchedule(() => _alarms[index] = schedule.Alarms);
            return Respond(HttpStatusCode.OK, SettingsJson, "Saved the schedule; answered the board's settings", json: true, shownBody: Vm208TraceText.SettingsAnswer(200));
        }

        // The board's own pages, behind HTTP basic authentication (serveStatic(...).setAuthentication).
        if (method == "GET" && (path == "/" || path.EndsWith(".html", StringComparison.Ordinal)))
        {
            return Respond(HttpStatusCode.Unauthorized, "", "Asks for the web page's login", isError: true, extraHeader: "WWW-Authenticate: Basic realm=\"Login Required\"\r\n");
        }

        // Pulses, timers, /setalarm and settings aren't simulated; the app never sends them.
        return Respond(HttpStatusCode.NotFound, "", "Not found", isError: true);
    }

    // The Content-Type as ESPAsyncWebServer keeps it (AsyncWebServerRequest::_parseReqHeader): the line trimmed first
    // (WebRequest.cpp, _temp.trim()), a header whose name matches in any case, its value from two characters after the
    // colon, up to a ';'. The last one wins.
    private static string ContentType(IEnumerable<string> headerLines)
    {
        var contentType = "";
        foreach (var raw in headerLines)
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                var value = colon + 2 < line.Length ? line[(colon + 2)..] : "";
                var semicolon = value.IndexOf(';', StringComparison.Ordinal);
                contentType = semicolon < 0 ? value : value[..semicolon];
            }
        }

        return contentType;
    }

    // Whether ESPAsyncWebServer reads the body as a form (AsyncWebServerRequest::_parseReqBody, _isPlainPost): a
    // Content-Type that starts with application/x-www-form-urlencoded, compared case-sensitively, or exactly
    // text/plain with a body that starts like "name=". The firmware looks at the body's first TCP segment; the
    // simulator at the whole body, which is the same for a request as small as the app's.
    private static bool IsPlainPost(string contentType, string body)
    {
        if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.Ordinal))
        {
            return true;
        }

        if (contentType != "text/plain" || body.Length == 0 || !IsParamChar(body[0]))
        {
            return false;
        }

        // As the library's loop: i ends one past the first character that can't be in a name.
        var i = 0;
        while (i < body.Length && IsParamChar(body[i++]))
        {
        }

        return i < body.Length && body[i - 1] == '=';
    }

    // __is_param_char in ESPAsyncWebServer's WebRequest.cpp.
    private static bool IsParamChar(char c)
    {
        return c is not ('\0' or '{' or '[' or '&' or '=');
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

    // Counts BusyNext down while it is above 0; 0 or less answers normally.
    private bool TakeBusy()
    {
        while (true)
        {
            var left = Volatile.Read(ref _busyNext);
            if (left <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _busyNext, left - 1, left) == left)
            {
                return true;
            }
        }
    }

    private byte[] RespondStatus()
    {
        if (TakeBusy())
        {
            return Respond(HttpStatusCode.OK, """{"Channels":[]}""", "Busy: no relays sent", isError: true, json: true);
        }

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
        string extraHeader = "",
        string? shownBody = null)
    {
        Trace(BoardTraceDirection.Received, text, shownBody ?? Vm208TraceText.Answer((int)statusCode, body), isError, isRoutine);
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

    // A made-up answer of sendSettings (src/Network/server_VM208.cpp): what the firmware answers a saved schedule.
    private const string SettingsJson = """{"USERNAME":"admin","BOARDNAME":"VM208","SSID":"","TIMEZONE":0,"DST":0}""";

    private static IReadOnlyList<Vm208Alarm> Enabled(IReadOnlyList<Vm208Alarm> alarms, bool enabled)
    {
        return [.. alarms.Select(a => a with { Enabled = enabled })];
    }

    // A change of the schedules: tells the page when the relays with an enabled alarm changed.
    private void ChangeSchedule(Action change)
    {
        bool changed;
        lock (_lock)
        {
            var before = _alarms.ToArray();
            change();
            changed = !before.Zip(_alarms).All(p => p.First.SequenceEqual(p.Second));
        }

        if (changed)
        {
            RaiseChanged();
        }
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
                Vm208Log.SubscriberFailed(_logger, ex);
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
                Vm208Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
