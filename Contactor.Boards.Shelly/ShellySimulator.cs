using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Shelly;

/// <summary>
/// A fake Shelly Gen2+ device on a local TCP port: a small HTTP/1.1 server with <c>POST /rpc</c> as the docs describe
/// it ("RPC Channels", "RPC Protocol"), answering <c>Shelly.GetDeviceInfo</c>, <c>Shelly.GetStatus</c>,
/// <c>Switch.Set</c>, and <c>Switch.GetConfig</c> and <c>Switch.SetConfig</c> for each switch's <c>initial_state</c>,
/// with 1 to 4 switches, and one request per connection. Every change of a switch's settings grows <c>sys.cfg_rev</c>,
/// as "Sys" says; a setting set to what it was doesn't. It holds each switch's <c>auto_on</c> and schedule jobs, with
/// <c>Schedule.List</c> and <c>Schedule.Update</c>, and <c>sys.schedule_rev</c>, which grows with every update. With a password it asks for the digest of
/// "Authentication": in the way of firmware 2.0.0 and later (a nonce for many requests with a growing <c>nc</c>, stale
/// after an hour or 30,000 requests, and 429 after too many wrong passwords) or, for an older <see cref="Firmware"/>,
/// in the way before it (a new challenge for every request). Its generation, firmware and profile can be set, for a
/// device the app must refuse. It never tells the app anything by itself. Used by the tests and, in development, by
/// Contactor.Simulator. It listens on the loopback interface only.
/// </summary>
public sealed class ShellySimulator : ISimulatedBoard, ISimulatedBoardRejectConnections
{
    /// <summary>How long a client may take to send its whole request before the connection is closed.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>"Each nonce is valid for 1 hour from the time it was issued" ("Authentication", firmware 2.0.0+).</summary>
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromHours(1);

    /// <summary>"Failed authentication attempts are tracked using a 10-minute sliding window".</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    /// <summary>"A single nonce can be reused for up to 30,000 requests".</summary>
    public const int MaxNonceUses = 30_000;

    // "The device maintains a circular buffer of up to 32 nonce entries".
    private const int NonceSlots = 32;

    // A request head or body longer than this isn't one the app sends.
    private const int MaxRequestHeadBytes = 8192;
    private const int MaxRequestBodyBytes = 16384;

    // Methods kept for the tests; a long-running simulator keeps only the newest.
    private const int ReceivedKept = 1000;

    // The app shows as connected while it asked something this recently: it opens a connection for every request.
    private static readonly TimeSpan ClientWindow = TimeSpan.FromSeconds(10);

    private readonly BoardModel _model;
    private string _password;
    private readonly ILogger _logger;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _received = new();
    private readonly Lock _lock = new();

    // Under _lock: the nonces handed out, oldest first, and the wrong passwords within the window.
    private readonly List<NonceEntry> _nonces = [];
    private readonly Queue<DateTimeOffset> _failures = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly string[] _initialStates;
    private readonly bool[] _autoOn;
    private readonly List<SimulatedJob> _jobs = [];
    private long _scheduleRevision;
    private long _nextJobId = 1;
    private byte _relays;
    private byte _lastStatusSent = byte.MaxValue;
    private long _configRevision = 10;
    private int _deniedCount;
    private int _challengeCount;
    private int _staleCount;
    private int _throttledCount;
    private int _requestCount;

    /// <summary>A device with this entry's switches and no password.</summary>
    public ShellySimulator(BoardModel model)
        : this(model, null, NullLogger.Instance)
    {
    }

    /// <summary>A device with this entry's switches and this password, or none if it is null or empty.</summary>
    public ShellySimulator(BoardModel model, string? password, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        if (!ShellyModels.IsShelly(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Shelly.", nameof(model));
        }

        _model = model;
        _password = password ?? "";
        _logger = logger;
        _deviceId = string.Create(CultureInfo.InvariantCulture, $"shellysim{model.Capabilities.RelayCount}-0a1b2c3d4e5f");
        _initialStates = [.. Enumerable.Repeat("off", model.Capabilities.RelayCount)];
        _autoOn = new bool[model.Capabilities.RelayCount];
    }

    public event EventHandler? Changed;

    public event EventHandler<BoardTraceEntry>? Traced;

    public BoardModel Model => _model;

    /// <summary>The clock for nonces, wrong passwords, the traffic's times and who counts as connected.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public int Port { get; private set; }

    public bool HasLogin => Volatile.Read(ref _password).Length > 0;

    /// <summary>The device id, which is also the digest realm.</summary>
    public string DeviceId => _deviceId;

    /// <summary>
    /// The generation <c>Shelly.GetDeviceInfo</c> gives, 2 by default. Null: a Gen1 device, which has no <c>/rpc</c>
    /// and answers every request with 404.
    /// </summary>
    public int? Generation { get; set; } = 2;

    /// <summary>
    /// The firmware version, <c>ver</c>: "2.0.0" by default. Before 2.0.0 the digest is the old way, a new challenge
    /// for every request.
    /// </summary>
    public string Firmware { get; set; } = "2.0.0";

    /// <summary>The device profile, e.g. "switch" or "cover"; null (the default) for a device with one profile.</summary>
    public string? Profile { get; set; }

    /// <summary>When true, new connections are closed at once, as if the device were unreachable.</summary>
    public bool RejectConnections { get; set; }

    /// <summary>When true, every request that needs the password gets 429, as a device that throttles.</summary>
    public bool Throttle { get; set; }

    /// <summary>
    /// Called with the relay after each <c>Switch.Set</c> the device carried out, before it answers: a test changes the
    /// device there between two requests of one command, e.g. with <see cref="Throttle"/> or <see cref="RejectConnections"/>.
    /// </summary>
    public Action<int>? AfterSwitchSet { get; set; }

    /// <summary>
    /// The relays whose <c>Switch.Set</c> the device refuses with <see cref="Refusal"/>, as with "Overtemp condition
    /// present". Bit 0 = relay 1.
    /// </summary>
    public byte FailRelays { get; set; }

    /// <summary>
    /// The relays whose <c>Switch.GetConfig</c> and <c>Switch.SetConfig</c> the device refuses with <see cref="Refusal"/>.
    /// Bit 0 = relay 1.
    /// </summary>
    public byte FailConfigRelays { get; set; }

    /// <summary>
    /// The relays whose <c>Switch.GetConfig</c> gets an answer cut short, which isn't JSON. Bit 0 = relay 1.
    /// </summary>
    public byte UnreadableConfigRelays { get; set; }

    /// <summary>
    /// The relays whose <c>Switch.GetConfig</c> the device never answers, holding the connection until the simulator
    /// stops. Bit 0 = relay 1.
    /// </summary>
    public byte SilentConfigRelays { get; set; }

    /// <summary>Whether the status has <c>sys.cfg_rev</c>; true by default. False: a device that gives none.</summary>
    public bool ReportsConfigRevision { get; set; } = true;

    /// <summary>Whether the status has <c>sys.schedule_rev</c>; true by default. False: a device that gives none.</summary>
    public bool ReportsScheduleRevision { get; set; } = true;

    /// <summary>When true, <c>Schedule.List</c> is refused with <see cref="Refusal"/>.</summary>
    public bool FailSchedules { get; set; }

    /// <summary>The schedules whose <c>Schedule.Update</c> is refused with <see cref="Refusal"/>, by job id.</summary>
    public IReadOnlySet<long> FailScheduleUpdates { get; set; } = new HashSet<long>();

    /// <summary>
    /// The schedules whose <c>Schedule.Update</c> the device never answers, holding the connection until the simulator
    /// stops, by job id.
    /// </summary>
    public IReadOnlySet<long> SilentScheduleUpdates { get; set; } = new HashSet<long>();

    /// <summary>The error a relay in <see cref="FailRelays"/> gets: -109 "Overtemp condition present" ("Common Errors").</summary>
    public ShellyRpcError Refusal { get; set; } = new(ShellyApi.FailedPrecondition, "Overtemp condition present");

    /// <summary>Requests refused for a wrong password or digest.</summary>
    public int DeniedCount => Volatile.Read(ref _deniedCount);

    /// <summary>401 challenges sent, stale ones included.</summary>
    public int ChallengeCount => Volatile.Read(ref _challengeCount);

    /// <summary>401 challenges with <c>stale=true</c> sent.</summary>
    public int StaleCount => Volatile.Read(ref _staleCount);

    /// <summary>429 answers sent.</summary>
    public int ThrottledCount => Volatile.Read(ref _throttledCount);

    /// <summary>Requests answered since start.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// The methods of the requests received, as they arrived, each with " (signed)" when it had an
    /// <c>Authorization</c> header: the newest 1,000.
    /// </summary>
    public IReadOnlyList<string> Received => [.. _received];

    /// <summary>Its relays; it has no inputs. The clients are the addresses that asked something within the last 10 s.</summary>
    public SimulatedBoardState State
    {
        get
        {
            var since = TimeProvider.GetUtcNow() - ClientWindow;
            lock (_lock)
            {
                return new SimulatedBoardState(
                    new ChannelMask(_relays),
                    ChannelMask.None,
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

    /// <summary><c>sys.cfg_rev</c>: 10 at start, 1 more for every change of a switch's settings.</summary>
    public long ConfigRevision
    {
        get
        {
            lock (_lock)
            {
                return _configRevision;
            }
        }
    }

    /// <summary>A relay's <c>initial_state</c>, as <c>Switch.GetConfig</c> gives it: "off" at start.</summary>
    public string InitialState(int relay)
    {
        _model.Capabilities.Relay(relay);
        lock (_lock)
        {
            return _initialStates[relay - 1];
        }
    }

    /// <summary>
    /// Sets a relay's <c>initial_state</c> "at the device", e.g. on its own web page: any text, also one the docs don't
    /// list. Grows <c>cfg_rev</c> when it changed.
    /// </summary>
    public void SetInitialState(int relay, string initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        _model.Capabilities.Relay(relay);
        if (ChangeInitialState(relay, initialState))
        {
            RaiseChanged();
        }
    }

    /// <summary>Another setting changes "at the device", e.g. a switch's name: <c>cfg_rev</c> grows, and nothing else.</summary>
    public void ChangeAnotherSetting()
    {
        lock (_lock)
        {
            _configRevision++;
        }
    }

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

    /// <summary><c>sys.schedule_rev</c>: 0 at start, 1 more for every job added and every update.</summary>
    public long ScheduleRevision
    {
        get
        {
            lock (_lock)
            {
                return _scheduleRevision;
            }
        }
    }

    /// <summary>A relay's <c>auto_on</c>, as <c>Switch.GetConfig</c> gives it: false at start.</summary>
    public bool AutoOn(int relay)
    {
        _model.Capabilities.Relay(relay);
        lock (_lock)
        {
            return _autoOn[relay - 1];
        }
    }

    /// <summary>Sets a relay's <c>auto_on</c> "at the device"; grows <c>cfg_rev</c> when it changed.</summary>
    public void SetAutoOn(int relay, bool autoOn)
    {
        _model.Capabilities.Relay(relay);
        if (ChangeAutoOn(relay, autoOn))
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Adds a schedule job "at the device", as <c>Schedule.Create</c> would: <paramref name="calls"/> is the JSON array of
    /// its calls, as the docs write them, e.g. <c>[{"method":"Switch.Set","params":{"id":0,"on":true}}]</c>, taken as it
    /// is. Grows <c>schedule_rev</c>. Returns the new job's id: 1 for the first, then 1 more each.
    /// </summary>
    public long AddSchedule(string calls, bool enable = true, string timespec = "0 0 8 * * *")
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(timespec);
        using (var document = JsonDocument.Parse(calls))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("The calls are a JSON array.", nameof(calls));
            }
        }

        long id;
        lock (_lock)
        {
            id = _nextJobId++;
            _jobs.Add(new SimulatedJob(id, timespec, calls) { Enable = enable });
            _scheduleRevision++;
        }

        RaiseChanged();
        return id;
    }

    /// <summary>Turns a job on or off "at the device"; grows <c>schedule_rev</c>, as every update does.</summary>
    public void SetScheduleEnabled(long id, bool enable)
    {
        lock (_lock)
        {
            Job(id).Enable = enable;
            _scheduleRevision++;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Turns a job on or off "at the device" when it isn't already, as one step under the device's lock; grows
    /// <c>schedule_rev</c> only then. Whether it changed: false for a job it doesn't hold, too.
    /// </summary>
    public bool TrySetScheduleEnabled(long id, bool enable)
    {
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == id);
            if (job is null || job.Enable == enable)
            {
                return false;
            }

            job.Enable = enable;
            _scheduleRevision++;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>The schedule jobs, oldest first.</summary>
    public IReadOnlyList<ShellySimulatedSchedule> Schedules
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs.Select(j => new ShellySimulatedSchedule(j.Id, j.Enable, j.Timespec, j.Calls))];
            }
        }
    }

    /// <summary>Whether a job is enabled.</summary>
    public bool ScheduleEnabled(long id)
    {
        lock (_lock)
        {
            return Job(id).Enable;
        }
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        var mask = (byte)_model.Capabilities.Relay(relay).Bits;
        Change(() => _relays = turnOn ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
        return Task.CompletedTask;
    }

    /// <summary>The app doesn't read a Shelly's inputs: there are none to set.</summary>
    public Task SetInputAsync(int input, bool closed)
    {
        throw new ArgumentOutOfRangeException(nameof(input), input, "The app reads no inputs of a Shelly.");
    }

    /// <summary>Lets every nonce expire, as an hour later: the next request with one gets a 401 with <c>stale=true</c>.</summary>
    public void ExpireNonces()
    {
        lock (_lock)
        {
            foreach (var nonce in _nonces)
            {
                nonce.Expired = true;
            }
        }
    }

    /// <summary>
    /// Changes the password, as a person would on the device's own web page while the app runs: a kept nonce still
    /// answers, but the old password is wrong from now on. Not to or from no password.
    /// </summary>
    public void ChangePassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!HasLogin)
        {
            throw new InvalidOperationException("The device has no password to change.");
        }

        Volatile.Write(ref _password, password);
    }

    /// <summary>Forgets every nonce, as after a reboot: the next request with one gets a plain 401.</summary>
    public void ForgetNonces()
    {
        lock (_lock)
        {
            _nonces.Clear();
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
            HttpRequest? request;

            // A client that connects and says nothing must not hold a handler until the simulator stops.
            using (var timeout = new CancellationTokenSource(RequestTimeout, TimeProvider))
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                request = await ReadRequestAsync(stream, reading.Token).ConfigureAwait(false);
            }

            if (request is null)
            {
                // Too long, cut short or not HTTP: closed without an answer.
                Trace(BoardTraceDirection.None, "A request the device doesn't understand: connection closed", null, isError: true);
                return;
            }

            var response = Handle(request);
            Interlocked.Increment(ref _requestCount);
            if (response is null)
            {
                // Never answered (SilentConfigRelays, SilentScheduleUpdates): held until the simulator stops.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return;
            }

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

    // The request line, the headers the device reads and the body (by Content-Length); null for a request that is too
    // long, cut short or badly formed.
    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes + MaxRequestBodyBytes];
        var length = 0;
        var headEnd = -1;
        while (headEnd < 0)
        {
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
            headEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.Latin1.GetString(buffer, 0, headEnd).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return null;
        }

        string? authorization = null;
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return null;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength > MaxRequestBodyBytes))
            {
                return null;
            }
        }

        var bodyStart = headEnd + 4;
        var total = bodyStart + contentLength;
        while (length < total)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, total - length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        return new HttpRequest(parts[0], parts[1], authorization, buffer[bodyStart..total]);
    }

    // Null for a request the device never answers.
    private byte[]? Handle(HttpRequest http)
    {
        var signed = http.Authorization is not null;
        var rpc = http.Method == "POST" && http.Target == ShellyApi.RpcPath ? ShellyJson.TryReadRequest(http.Body) : null;
        var (relay, on) = SwitchParameters(rpc);
        var initialState = InitialStateParameter(rpc);
        var autoOn = AutoOnParameter(rpc);
        var job = JobParameter(rpc);
        var text = rpc is null
            ? $"{ShellyRpcError.Printable(http.Method)} {ShellyRpcError.Printable(http.Target)}"
            : rpc.Method == ShellyApi.SwitchSetConfig && relay is { } setting && ShellyApi.ToPowerOnState(initialState) is { } state
                ? ShellyTraceText.SetPowerOn(setting, state)
                : rpc.Method == ShellyApi.SwitchSetConfig && relay is { } autoOnRelay && autoOn == false
                    ? ShellyTraceText.TurnOffAutoOn(autoOnRelay)
                    : rpc.Method == ShellyApi.ScheduleUpdate && job is { } jobId && EnableParameter(rpc) == false
                        ? ShellyTraceText.TurnOffSchedule(jobId)
                        : ShellyTraceText.Request(rpc.Method, relay ?? 0, on ?? false);
        Trace(BoardTraceDirection.Sent, text, ShellyTraceText.RequestBytes(http.Body, signed), isRoutine: rpc?.Method == ShellyApi.GetStatus);
        _received.Enqueue(rpc is null ? "?" : rpc.Method + (signed ? " (signed)" : ""));
        while (_received.Count > ReceivedKept && _received.TryDequeue(out _))
        {
        }

        // A Gen1 device has no /rpc at all.
        if (Generation is null || http.Target != ShellyApi.RpcPath)
        {
            return Respond(HttpStatusCode.NotFound, "", "Not found", isError: true);
        }

        if (rpc is null)
        {
            // The docs don't say what a badly formed request gets; a plain 400 is enough for the app.
            return Respond(HttpStatusCode.BadRequest, "", "A request the device doesn't understand", isError: true);
        }

        // "When enabled, all communication is protected except … the RPC method Shelly.GetDeviceInfo".
        if (HasLogin && rpc.Method != ShellyApi.GetDeviceInfo && Authenticate(http.Authorization) is { } refusal)
        {
            return refusal;
        }

        return rpc.Method switch
        {
            ShellyApi.GetDeviceInfo => Result(rpc, WriteDeviceInfo, ShellyTraceText.DeviceInfo(new ShellyDeviceInfo(_deviceId, Model.Id, Generation, Firmware, Profile))),
            ShellyApi.GetStatus => RespondStatus(rpc),
            ShellyApi.SwitchSet => SetSwitch(rpc, relay, on),
            ShellyApi.SwitchGetConfig => GetConfig(rpc, relay),
            ShellyApi.SwitchSetConfig => SetConfig(rpc, relay, initialState, autoOn),
            ShellyApi.ScheduleList => ListSchedules(rpc),
            ShellyApi.ScheduleUpdate => UpdateSchedule(rpc, job),

            // Not from the docs read: the app sends no other method, and any error tells it so.
            _ => Error(rpc, new ShellyRpcError(404, $"No handler for {rpc.Method}")),
        };
    }

    // Null when the request may go on; otherwise the 401 or 429 to send.
    private byte[]? Authenticate(string? authorization)
    {
        var now = TimeProvider.GetUtcNow();
        var current = ShellyApi.ParseFirmware(Firmware) is { } firmware && firmware >= ShellyApi.NonceReuseFirmware;
        lock (_lock)
        {
            while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow)
            {
                _failures.Dequeue();
            }

            // "When throttled, the device returns HTTP status 429 (Too Many Requests) instead of 401."
            if (Throttle || (current && _failures.Count > 0 && now - _failures.Last() < Backoff(_failures.Count)))
            {
                Interlocked.Increment(ref _throttledCount);
                return Respond(HttpStatusCode.TooManyRequests, "", "Too many requests: wait", isError: true);
            }

            var credentials = ShellyDigest.ParseAuthorization(authorization);
            if (credentials is null)
            {
                return Challenge(stale: false, current, "Asks for the password");
            }

            var entry = _nonces.FirstOrDefault(n => n.Nonce == credentials.Nonce);
            if (entry is null)
            {
                // A nonce the device never gave, or forgot: it can't tell whether the password was right.
                return Challenge(stale: false, current, "Unknown nonce: asks for the password again");
            }

            var right = credentials.Username == ShellyApi.Username
                && credentials.Realm == _deviceId
                && credentials.Uri == ShellyApi.RpcPath
                && credentials.Algorithm.Equals(ShellyDigest.Algorithm, StringComparison.OrdinalIgnoreCase)
                && credentials.Qop == "auth"
                && credentials.NonceCount.Length == 8
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(credentials.Response),
                    Encoding.ASCII.GetBytes(ShellyDigest.Response(ShellyApi.Username, _deviceId, Volatile.Read(ref _password), ShellyDigest.Method, ShellyApi.RpcPath, credentials.Nonce, credentials.NonceCount, credentials.ClientNonce)));
            if (!right)
            {
                Interlocked.Increment(ref _deniedCount);
                _failures.Enqueue(now);

                // More than 40 all wait the same: a client that keeps guessing can't grow the queue without end.
                while (_failures.Count > 100)
                {
                    _failures.Dequeue();
                }

                return Challenge(stale: false, current, "Wrong password", isError: true);
            }

            if (!current)
            {
                // Before 2.0.0 a nonce answers one request: the next one goes through the challenge again.
                if (entry.Uses > 0)
                {
                    return Challenge(stale: false, current, "Asks for the password again");
                }

                entry.Uses++;
                return null;
            }

            if (entry.Expired || now - entry.IssuedAt >= NonceLifetime || entry.Uses >= MaxNonceUses)
            {
                Interlocked.Increment(ref _staleCount);
                _nonces.Remove(entry);
                return Challenge(stale: true, current, "The nonce is stale: asks for a new one");
            }

            // "rejects requests with an nc value that is not strictly greater than the previously seen value".
            if (!int.TryParse(credentials.NonceCount, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var count) || count <= entry.LastCount)
            {
                return Challenge(stale: false, current, "A nonce count used before: asks for the password again", isError: true);
            }

            entry.LastCount = count;
            entry.Uses++;
            if (count == 1)
            {
                // "Successful authentication with a fresh nonce resets the failure counters."
                _failures.Clear();
            }

            return null;
        }
    }

    // "11-20 failures: 10 seconds between attempts; 21-30: 30 seconds; 31-40: 60 seconds; 40+: 5 minutes".
    private static TimeSpan Backoff(int failures)
    {
        return failures switch
        {
            <= 10 => TimeSpan.Zero,
            <= 20 => TimeSpan.FromSeconds(10),
            <= 30 => TimeSpan.FromSeconds(30),
            <= 40 => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromMinutes(5),
        };
    }

    // Under _lock: a 401 with a new nonce, base64 from 2.0.0 and a number before, as "Authentication" shows them.
    private byte[] Challenge(bool stale, bool current, string text, bool isError = false)
    {
        var nonce = current
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            : RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        if (_nonces.Count >= NonceSlots)
        {
            _nonces.RemoveAt(0);
        }

        _nonces.Add(new NonceEntry(nonce, TimeProvider.GetUtcNow()));
        Interlocked.Increment(ref _challengeCount);
        return Respond(HttpStatusCode.Unauthorized, "", text, isError: isError, header: $"WWW-Authenticate: {ShellyDigest.Challenge(_deviceId, nonce, stale)}");
    }

    private void WriteDeviceInfo(Utf8JsonWriter writer)
    {
        // The fields of "Shelly.GetDeviceInfo", in the order of the docs' /shelly example.
        writer.WriteString("id", _deviceId);
        writer.WriteString("mac", "0A1B2C3D4E5F");
        writer.WriteString("model", string.Create(CultureInfo.InvariantCulture, $"SIM-{_model.Capabilities.RelayCount}"));
        if (Generation is { } gen && gen >= 2)
        {
            writer.WriteNumber("gen", gen);
        }

        writer.WriteString("fw_id", $"20260101-000000/{Firmware}-sim");
        writer.WriteString("ver", Firmware);
        writer.WriteString("app", "Simulator");
        if (Profile is not null)
        {
            writer.WriteString("profile", Profile);
        }

        writer.WriteBoolean("auth_en", HasLogin);
        if (HasLogin)
        {
            writer.WriteString("auth_domain", _deviceId);
        }
        else
        {
            writer.WriteNull("auth_domain");
        }
    }

    private byte[] RespondStatus(ShellyRequest rpc)
    {
        byte relays;
        bool routine;
        lock (_lock)
        {
            relays = _relays;
            routine = relays == _lastStatusSent;
            _lastStatusSent = relays;
        }

        var cover = string.Equals(Profile, "cover", StringComparison.OrdinalIgnoreCase);
        var body = ShellyJson.WriteResult(rpc.Id, _deviceId, rpc.Source, writer =>
        {
            writer.WriteStartObject("ble");
            writer.WriteEndObject();
            if (cover)
            {
                // A cover in the cover profile, no switches; what it holds isn't read by the app.
                writer.WriteStartObject("cover:0");
                writer.WriteNumber("id", 0);
                writer.WriteString("state", "stopped");
                writer.WriteEndObject();
            }
            else
            {
                for (var i = 0; i < _model.Capabilities.RelayCount; i++)
                {
                    // As the docs' Shelly.GetStatus example: id, source, output, temperature.
                    writer.WriteStartObject(string.Create(CultureInfo.InvariantCulture, $"{ShellyApi.SwitchKeyPrefix}{i}"));
                    writer.WriteNumber("id", i);
                    writer.WriteString("source", "init");
                    writer.WriteBoolean("output", (relays & (1 << i)) != 0);
                    writer.WriteStartObject("temperature");
                    writer.WriteNumber("tC", 23.5);
                    writer.WriteNumber("tF", 74.4);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }

            writer.WriteStartObject("sys");
            writer.WriteString("mac", "0A1B2C3D4E5F");
            writer.WriteBoolean("restart_required", false);
            if (ReportsConfigRevision)
            {
                writer.WriteNumber(ShellyApi.ConfigRevision, ConfigRevision);
            }

            if (ReportsScheduleRevision)
            {
                writer.WriteNumber(ShellyApi.ScheduleRevision, ScheduleRevision);
            }
            writer.WriteEndObject();
        });
        var text = cover ? "Status: a cover, no relays" : ShellyTraceText.Status(new ShellyStatus(relays), _model.Capabilities.RelayCount);
        return Respond(HttpStatusCode.OK, body, text, isRoutine: routine);
    }

    // As "Switch.Set": "id" and "on" required; -103 without them, -105 "Bad id=N" for a switch it hasn't.
    private byte[] SetSwitch(ShellyRequest rpc, int? relay, bool? on)
    {
        if (relay is null || on is null)
        {
            return Error(rpc, new ShellyRpcError(-103, relay is null ? "Missing required 'id'" : "Missing required 'on'"));
        }

        if (relay < 1 || relay > _model.Capabilities.RelayCount || string.Equals(Profile, "cover", StringComparison.OrdinalIgnoreCase))
        {
            return Error(rpc, new ShellyRpcError(-105, string.Create(CultureInfo.InvariantCulture, $"Bad id={relay - 1}")));
        }

        var mask = (byte)(1 << (relay.Value - 1));
        if ((FailRelays & mask) != 0)
        {
            return Error(rpc, Refusal);
        }

        bool wasOn;
        lock (_lock)
        {
            wasOn = (_relays & mask) != 0;
        }

        Change(() => _relays = on.Value ? (byte)(_relays | mask) : (byte)(_relays & ~mask));
        AfterSwitchSet?.Invoke(relay.Value);
        return Result(rpc, writer => writer.WriteBoolean("was_on", wasOn), ShellyTraceText.Switched(relay.Value, on.Value, wasOn));
    }

    // As "Switch.GetConfig": "id" required; -103 without it, -105 "Bad id=N" for a switch it hasn't (the docs' example
    // for Input.GetConfig). The members in the order of the docs' example; only initial_state is the device's own.
    private byte[]? GetConfig(ShellyRequest rpc, int? relay)
    {
        if (CheckSwitch(rpc, relay) is { } refusal)
        {
            return refusal;
        }

        var bit = 1 << (relay!.Value - 1);
        if ((SilentConfigRelays & bit) != 0)
        {
            Trace(BoardTraceDirection.None, "No answer to the settings request", null, isError: true);
            return null;
        }

        if ((UnreadableConfigRelays & bit) != 0)
        {
            return Respond(HttpStatusCode.OK, "{\"id\":", "An answer cut short", isError: true);
        }

        var initialState = InitialState(relay!.Value);
        var autoOn = AutoOn(relay.Value);
        return Result(
            rpc,
            writer =>
            {
                writer.WriteNumber("id", relay.Value - 1);
                writer.WriteNull("name");
                writer.WriteString("in_mode", "follow");
                writer.WriteBoolean("in_locked", false);
                writer.WriteString(ShellyApi.InitialState, initialState);
                writer.WriteBoolean(ShellyApi.AutoOn, autoOn);
                writer.WriteNumber("auto_on_delay", 60);
                writer.WriteBoolean("auto_off", false);
                writer.WriteNumber("auto_off_delay", 60);
                writer.WriteBoolean("power_restore_delay_enable", false);
                writer.WriteNumber("power_restore_delay", 60);
            },
            ShellyTraceText.SettingsRead(relay.Value, ShellyApi.ToPowerOnState(initialState), autoOn));
    }

    // As "Switch.SetConfig": "id" and "config" required; only initial_state and auto_on are taken, and only a value the
    // docs list (the docs don't say what another one gets: -103 is "Common Errors"' error for parameters that don't
    // match). Its result is the docs' {"restart_required":false}.
    private byte[] SetConfig(ShellyRequest rpc, int? relay, string? initialState, bool? autoOn)
    {
        if (CheckSwitch(rpc, relay) is { } refusal)
        {
            return refusal;
        }

        if (rpc.Params is not { } parameters || !parameters.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return Error(rpc, new ShellyRpcError(-103, "Missing required 'config'"));
        }

        if (config.TryGetProperty(ShellyApi.InitialState, out _) && ShellyApi.ToPowerOnState(initialState) is null)
        {
            return Error(rpc, new ShellyRpcError(-103, "Invalid argument 'initial_state'"));
        }

        if (config.TryGetProperty(ShellyApi.AutoOn, out _) && autoOn is null)
        {
            return Error(rpc, new ShellyRpcError(-103, "Invalid argument 'auto_on'"));
        }

        if (autoOn is { } newAutoOn && ChangeAutoOn(relay!.Value, newAutoOn))
        {
            RaiseChanged();
        }

        if (initialState is not null)
        {
            if (ChangeInitialState(relay!.Value, initialState!))
            {
                RaiseChanged();
            }
        }

        return Result(rpc, writer => writer.WriteBoolean("restart_required", false), ShellyTraceText.SettingSaved(relay!.Value));
    }

    // As "Schedule.List": every job in the order it was made, with "id", "enable", "timespec" and "calls", and "rev".
    private byte[] ListSchedules(ShellyRequest rpc)
    {
        if (FailSchedules)
        {
            return Error(rpc, Refusal);
        }

        List<SimulatedJob> jobs;
        long revision;
        lock (_lock)
        {
            jobs = [.. _jobs.Select(j => new SimulatedJob(j.Id, j.Timespec, j.Calls) { Enable = j.Enable })];
            revision = _scheduleRevision;
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"{jobs.Count} schedules, {jobs.Count(j => j.Enable)} enabled");
        return Result(
            rpc,
            writer =>
            {
                writer.WriteStartArray("jobs");
                foreach (var job in jobs)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", job.Id);
                    writer.WriteBoolean("enable", job.Enable);
                    writer.WriteString("timespec", job.Timespec);
                    writer.WritePropertyName("calls");
                    writer.WriteRawValue(job.Calls);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("rev", revision);
            },
            text);
    }

    // As "Schedule.Update": "id" required (-103 without it); the docs don't say what an unknown id gets, so -105 as for
    // a switch it hasn't. Only "enable" is taken; every update grows schedule_rev ("incremented on every update").
    // Its result is {"rev":n}.
    private byte[]? UpdateSchedule(ShellyRequest rpc, long? id)
    {
        if (id is not { } jobId)
        {
            return Error(rpc, new ShellyRpcError(-103, "Missing required 'id'"));
        }

        if (FailScheduleUpdates.Contains(jobId))
        {
            return Error(rpc, Refusal);
        }

        if (SilentScheduleUpdates.Contains(jobId))
        {
            Trace(BoardTraceDirection.None, "No answer to the schedule update", null, isError: true);
            return null;
        }

        var enable = EnableParameter(rpc);
        if (rpc.Params is { } parameters && parameters.TryGetProperty("enable", out _) && enable is null)
        {
            return Error(rpc, new ShellyRpcError(-103, "Invalid argument 'enable'"));
        }

        long revision;
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == jobId);
            if (job is null)
            {
                return Error(rpc, new ShellyRpcError(-105, string.Create(CultureInfo.InvariantCulture, $"Bad id={jobId}")));
            }

            if (enable is { } value)
            {
                job.Enable = value;
            }

            revision = ++_scheduleRevision;
        }

        RaiseChanged();
        return Result(rpc, writer => writer.WriteNumber("rev", revision), string.Create(CultureInfo.InvariantCulture, $"Schedule {jobId} saved"));
    }

    // Under _lock: the job with this id; throws for one it hasn't.
    private SimulatedJob Job(long id)
    {
        return _jobs.Find(j => j.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such schedule.");
    }

    // Sets a relay's auto_on; cfg_rev grows only when it changed. Whether it changed.
    private bool ChangeAutoOn(int relay, bool autoOn)
    {
        lock (_lock)
        {
            if (_autoOn[relay - 1] == autoOn)
            {
                return false;
            }

            _autoOn[relay - 1] = autoOn;
            _configRevision++;
            return true;
        }
    }

    // -103 without an id, -105 for a switch it hasn't, and the refusal for a relay in FailConfigRelays; null to go on.
    private byte[]? CheckSwitch(ShellyRequest rpc, int? relay)
    {
        if (relay is null)
        {
            return Error(rpc, new ShellyRpcError(-103, "Missing required 'id'"));
        }

        if (relay < 1 || relay > _model.Capabilities.RelayCount || string.Equals(Profile, "cover", StringComparison.OrdinalIgnoreCase))
        {
            return Error(rpc, new ShellyRpcError(-105, string.Create(CultureInfo.InvariantCulture, $"Bad id={relay - 1}")));
        }

        return (FailConfigRelays & (1 << (relay.Value - 1))) != 0 ? Error(rpc, Refusal) : null;
    }

    // Sets a relay's initial_state; cfg_rev grows only when it changed. Whether it changed.
    private bool ChangeInitialState(int relay, string initialState)
    {
        lock (_lock)
        {
            if (_initialStates[relay - 1] == initialState)
            {
                return false;
            }

            _initialStates[relay - 1] = initialState;
            _configRevision++;
            return true;
        }
    }

    // Switch.SetConfig's config.initial_state; null when missing or not a string.
    private static string? InitialStateParameter(ShellyRequest? rpc)
    {
        return rpc?.Method == ShellyApi.SwitchSetConfig
            && rpc.Params is { } parameters
            && parameters.TryGetProperty("config", out var config)
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(ShellyApi.InitialState, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    // Switch.SetConfig's config.auto_on; null when missing or not true or false.
    private static bool? AutoOnParameter(ShellyRequest? rpc)
    {
        return rpc?.Method == ShellyApi.SwitchSetConfig
            && rpc.Params is { } parameters
            && parameters.TryGetProperty("config", out var config)
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(ShellyApi.AutoOn, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }

    // Schedule.Update's id; null when missing or not a whole number.
    private static long? JobParameter(ShellyRequest? rpc)
    {
        return rpc?.Method == ShellyApi.ScheduleUpdate
            && rpc.Params is { } parameters
            && parameters.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var number)
                ? number
                : null;
    }

    // Schedule.Update's enable; null when missing or not true or false.
    private static bool? EnableParameter(ShellyRequest? rpc)
    {
        return rpc?.Params is { } parameters
            && parameters.TryGetProperty("enable", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }

    // A Switch method's id as a relay (id + 1), and Switch.Set's on; null for one missing or of another type.
    private static (int? Relay, bool? On) SwitchParameters(ShellyRequest? rpc)
    {
        if (rpc?.Method is not (ShellyApi.SwitchSet or ShellyApi.SwitchGetConfig or ShellyApi.SwitchSetConfig) || rpc.Params is not { } parameters)
        {
            return (null, null);
        }

        int? relay = parameters.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number is >= 0 and < 1000
            ? number + 1
            : null;
        bool? on = parameters.TryGetProperty("on", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
        return (relay, on);
    }

    private byte[] Result(ShellyRequest rpc, Action<Utf8JsonWriter> result, string text)
    {
        return Respond(HttpStatusCode.OK, ShellyJson.WriteResult(rpc.Id, _deviceId, rpc.Source, result), text);
    }

    // "Common Errors": the frame comes with 200, as "RPC Channels" shows a POST /rpc answer.
    private byte[] Error(ShellyRequest rpc, ShellyRpcError error)
    {
        return Respond(HttpStatusCode.OK, ShellyJson.WriteError(rpc.Id, _deviceId, rpc.Source, error), $"Refused: {error}", isError: true);
    }

    private byte[] Respond(HttpStatusCode statusCode, string body, string text, bool isError = false, bool isRoutine = false, string? header = null)
    {
        Trace(BoardTraceDirection.Received, text, ShellyTraceText.Answer((int)statusCode, body), isError, isRoutine);
        var content = Encoding.UTF8.GetBytes(body);
        var extra = header is null ? "" : header + "\r\n";
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {(int)statusCode} {Reason(statusCode)}\r\nServer: ShellyHTTP/1.0.0\r\nContent-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extra}\r\n");
        return [.. Encoding.ASCII.GetBytes(head), .. content];
    }

    private static string Reason(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            _ => "Not Found",
        };
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
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated device.
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
#pragma warning disable CA1031 // A subscriber's bug must not take down the simulated device.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    private sealed record HttpRequest(string Method, string Target, string? Authorization, byte[] Body);

    // A schedule job: its calls as the JSON text they were given in.
    private sealed class SimulatedJob
    {
        public SimulatedJob(long id, string timespec, string calls)
        {
            Id = id;
            Timespec = timespec;
            Calls = calls;
        }

        public long Id { get; }

        public string Timespec { get; }

        public string Calls { get; }

        public bool Enable { get; set; }
    }

    private sealed class NonceEntry
    {
        public NonceEntry(string nonce, DateTimeOffset issuedAt)
        {
            Nonce = nonce;
            IssuedAt = issuedAt;
        }

        public string Nonce { get; }

        public DateTimeOffset IssuedAt { get; }

        public int LastCount { get; set; }

        public int Uses { get; set; }

        public bool Expired { get; set; }
    }
}
