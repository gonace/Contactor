using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.DAEnetIP2v2;

/// <summary>
/// HTTP client for the DAEnetIP2v2 legacy I/O API. It reads the controller's IO array and writes the P3/P5 output
/// bytes. One request at a time is coordinated by BoardPoller; commands are never queued while disconnected.
/// </summary>
public sealed partial class DAEnetIP2v2Client : IPolledBoard<DAEnetIP2v2Reading>, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly BoardModel _model;
    private readonly TimeProvider _time;
    private readonly ILogger<DAEnetIP2v2Client> _logger;
    private readonly BoardPoller<DAEnetIP2v2Reading> _poller;
    private readonly Lock _httpLock = new();
    private HttpClient? _http;
    private BoardEndpoint _endpoint;
    private BoardLogin _login = BoardLogin.None;

    public DAEnetIP2v2Client(IOptions<DAEnetIP2v2Options> options, BoardModel model, TimeProvider time, ILogger<DAEnetIP2v2Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (!DAEnetIP2v2Models.IsSupported(model)) throw new ArgumentException("Not a DAEnetIP2v2 model.", nameof(model));
        var settings = options.Value;
        if (settings.PollSeconds < 1) throw new ArgumentException("DAEnetIP2v2:PollSeconds must be at least 1.", nameof(options));
        _model = model;
        _login = new BoardLogin(settings.Username, settings.Password);
        _time = time;
        _logger = logger;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _poller = new BoardPoller<DAEnetIP2v2Reading>(this, time) { PollInterval = TimeSpan.FromSeconds(settings.PollSeconds) };
    }

    public event EventHandler<DAEnetIP2v2State>? StateChanged;
    public event EventHandler<BoardTraceEntry>? Traced;
    public BoardModel Model => _model;
    public DAEnetIP2v2State State
    {
        get
        {
            var state = _poller.State;
            return new(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);
    public BoardLogin Login => Volatile.Read(ref _login);

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint) return;
        _poller.Forget();
        _poller.Restart();
    }

    public void SetLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (Interlocked.Exchange(ref _login, login).Equals(login)) return;
        _poller.Forget();
        _poller.Restart();
    }

    public Task RunAsync(CancellationToken cancellationToken) => _poller.RunAsync(cancellationToken);

    public void Refresh() => _poller.PollNow();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Endpoint.HasHost) throw new InvalidOperationException("No controller address is configured.");
        await EnsureHttpAsync(cancellationToken).ConfigureAwait(false);
        Trace(BoardTraceDirection.None, "Connected to the controller", null, false, true);
    }

    public async Task<DAEnetIP2v2Reading> ReadAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync("/ioreg.js", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var reading = ParseIo(body, _model.Capabilities.RelayCount);
        Trace(BoardTraceDirection.Received, "Read relay states", null, false, true);
        return reading;
    }

    public void OnStateChanged(PolledState<DAEnetIP2v2Reading> state) =>
        StateChanged?.Invoke(this, new(state.IsConnected, state.Reading, state.LastContactUtc));

    public void OnStep(BoardPollStep kind, string? detail)
    {
        var text = kind switch
        {
            BoardPollStep.Connecting => "Connecting to the controller",
            BoardPollStep.Lost => $"Connection lost: {detail}",
            BoardPollStep.Waiting => $"Retrying in {detail} s",
            BoardPollStep.Stopped => "Connection stopped",
            _ => "Connection state changed",
        };
        Trace(BoardTraceDirection.None, text, null, kind == BoardPollStep.Lost, kind == BoardPollStep.Waiting);
    }

    public Task<ulong> TurnOnAsync(ulong mask, CancellationToken cancellationToken = default) =>
        ChangeAsync(mask, true, cancellationToken);

    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default) =>
        ChangeAsync(mask, false, cancellationToken);

    private async Task<ulong> ChangeAsync(ulong mask, bool on, CancellationToken cancellationToken)
    {
        if ((mask & ~_model.Capabilities.AllRelays.Bits) != 0)
            throw new ArgumentOutOfRangeException(nameof(mask), "The requested relay is not present on this model.");
        if (mask == 0) return 0;

        ulong confirmed = 0;
        await _poller.RunCommandAsync(async token =>
        {
            var query = new List<string>();
            foreach (var relay in new ChannelMask(mask).Channels)
            {
                var port = relay <= 8 ? 5 : 3;
                var channel = relay <= 8 ? relay : relay - 8;
                query.Add($"P{port}{channel}={(on ? 1 : 0).ToString(CultureInfo.InvariantCulture)}");
            }

            using var response = await Http.GetAsync("/iochange.cgi?ref=re-io&" + string.Join("&", query), token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Trace(BoardTraceDirection.Sent, on ? "Turn on relays" : "Turn off relays", null, false, false);
            var after = await ReadAsync(token).ConfigureAwait(false);
            confirmed = on ? mask & after.Relays : mask & ~after.Relays;
            if (confirmed == 0) throw new BoardRefusedException();
            return after;
        }, cancellationToken).ConfigureAwait(false);
        return confirmed;
    }

    private HttpClient Http
    {
        get
        {
            lock (_httpLock)
                return _http ?? throw new BoardNotConnectedException();
        }
    }

    private Task EnsureHttpAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_httpLock)
        {
            _http?.Dispose();
            var handler = new HttpClientHandler();
            var address = new UriBuilder(Uri.UriSchemeHttp, Endpoint.Host, Endpoint.Port).Uri;
            var client = new HttpClient(handler) { BaseAddress = address, Timeout = RequestTimeout };
            var login = Login;
            if (!login.IsNone)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.Username}:{login.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            _http = client;
        }
        return Task.CompletedTask;
    }

    /// <summary>Parses the documented IO array returned by /ioreg.js.</summary>
    public static DAEnetIP2v2Reading ParseIo(string body, int relayCount)
    {
        ArgumentNullException.ThrowIfNull(body);
        var match = IoArrayRegex().Match(body);
        if (!match.Success) throw new FormatException("The controller response did not contain the IO array.");
        var values = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length < 2) throw new FormatException("The controller IO array did not contain both digital ports.");
        // P5 carries relays 1–8; P3 carries relays 9–16 on the 16-relay kit.
        var bits = (ulong)values[1] | ((ulong)values[0] << 8);
        var mask = ChannelMask.FirstN(relayCount).Bits;
        return new DAEnetIP2v2Reading(bits & mask);
    }

    [GeneratedRegex(@"\bIO\s*=\s*\[([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IoArrayRegex();

    [LoggerMessage(Level = LogLevel.Debug, Message = "{BoardTrace}")]
    private partial void LogTrace(string boardTrace);

    private void Trace(BoardTraceDirection direction, string text, string? bytes, bool error, bool routine)
    {
        LogTrace(text);
        Traced?.Invoke(this, new BoardTraceEntry(_time.GetUtcNow(), direction, text, bytes, error, routine));
    }

    public void Dispose()
    {
        lock (_httpLock) { _http?.Dispose(); _http = null; }
        _poller.Dispose();
    }
}
