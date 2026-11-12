using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Vm204;

/// <summary>
/// Talks to a VM204 over its web API (<see cref="Vm204Api"/>): logs in for the key, polls the status through
/// <see cref="BoardPoller{TReading}"/> and switches relays. One request at a time (the poller sees to it), as the board
/// serves only three connections. Commands are never queued: without a connection they fail at once with
/// <see cref="BoardNotConnectedException"/>, a poll under way gives way to them, and the board's answer to one, its
/// status right after, is reported at once.
/// </summary>
public sealed class Vm204Client : IPolledBoard<Vm204Status>, IDisposable
{
    /// <summary>How long a request may take before it counts as unanswered.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private const int MaxResponseHeadersKilobytes = 8;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm204Client> _logger;
    private readonly HttpClient _http;
    private readonly BoardPoller<Vm204Status> _poller;

    private BoardEndpoint _endpoint;
    private Vm204Login _login;
    private string? _key;
    private Vm204Status? _lastTraced;

    public Vm204Client(IOptions<Vm204Options> options, TimeProvider timeProvider, ILogger<Vm204Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("Vm204:PollSeconds must be at least 1.", nameof(options));
        }

        // Fail at start, not on the first connection, if the login can't be sent.
        _login = Vm204Login.TryCreate(settings.Username, settings.Password, out var loginError)
            ?? throw new ArgumentException($"Vm204:Username or Vm204:Password can't be sent to the board: {loginError}", nameof(options));
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;

        // The board is on the local network: never through a proxy. Each answer closes the connection anyway.
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = MaxResponseHeadersKilobytes,
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            // The requests time out with _timeProvider instead, see SendAsync.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _poller = new BoardPoller<Vm204Status>(this, timeProvider, Vm204Status.SwitchesComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays or inputs change, and when the connection is lost or comes back; not on a poll that
    /// changed nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<Vm204State>? StateChanged;

    /// <summary>
    /// Every request and answer and every connection step, in plain words. The key, the user name and the password
    /// never show. On the poller's loop or a command's: handlers must be quick.
    /// </summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public Vm204State State
    {
        get
        {
            var state = _poller.State;
            return new Vm204State(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client connects: <c>Vm204:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>The login it uses: <c>Vm204:Username</c>/<c>Password</c> until <see cref="SetLogin"/> changes it.</summary>
    public Vm204Login Login => Volatile.Read(ref _login);

    /// <summary>Moves to another address at once; nothing happens if it is the same.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        _logger.EndpointChanged(endpoint.Host, endpoint.Port);
        Volatile.Write(ref _key, null);
        _poller.Restart();
    }

    /// <summary>Logs in again at once with another login; nothing happens if it is the same.</summary>
    public void SetLogin(Vm204Login login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (Interlocked.Exchange(ref _login, login).Equals(login))
        {
            return;
        }

        _logger.LoginChanged(login.IsNone);
        Volatile.Write(ref _key, null);
        _poller.Restart();
    }

    /// <summary>Connects and polls until cancelled. Never throws because of the board. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1, relays 1–4).</summary>
    public Task TurnOnAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(Vm204Api.RelayOnPath, mask, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1, relays 1–4).</summary>
    public Task TurnOffAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(Vm204Api.RelayOffPath, mask, cancellationToken);
    }

    /// <summary>Polls at once; the answer comes as <see cref="StateChanged"/> if anything changed.</summary>
    public void Refresh()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        _poller.PollNow();
    }

    public void Dispose()
    {
        _http.Dispose();
        _poller.Dispose();
    }

    // The poller calls these one at a time, never while a command runs.
    Task IPolledBoard<Vm204Status>.ConnectAsync(CancellationToken cancellationToken)
    {
        return LogInAsync(cancellationToken);
    }

    async Task<Vm204Status> IPolledBoard<Vm204Status>.ReadAsync(CancellationToken cancellationToken)
    {
        // The key is dropped when the board refuses it (someone made a new one on its web page): log in again.
        var key = Volatile.Read(ref _key) ?? await LogInAsync(cancellationToken).ConfigureAwait(false);
        var answer = await SendAsync($"{Vm204Api.StatusPath}?key={key}", routine: true, cancellationToken).ConfigureAwait(false);
        return ReadStatusAnswer(answer, key);
    }

    void IPolledBoard<Vm204Status>.OnStateChanged(PolledState<Vm204Status> state)
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        var vm204State = new Vm204State(state.IsConnected, state.Reading, state.LastContactUtc);

        // Each subscriber on its own: one that fails must not keep the state from the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<Vm204State>>())
        {
            try
            {
                handler(this, vm204State);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    void IPolledBoard<Vm204Status>.OnStep(BoardPollStep kind, string? detail)
    {
        switch (kind)
        {
            case BoardPollStep.Connecting:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, $"Connecting to {Endpoint}");
                break;
            case BoardPollStep.Lost:
                ForgetLastStatus();
                _logger.ConnectionLost(detail);
                Trace(BoardTraceDirection.None, $"Connection lost: {detail}", isError: true);
                break;
            case BoardPollStep.Waiting:
                _logger.Reconnecting(detail);
                Trace(BoardTraceDirection.None, $"Trying again in {detail} s");
                break;
            case BoardPollStep.Stopped:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, "The app closed the connection");
                break;
            default:
                break;
        }
    }

    private async Task CommandAsync(string path, byte mask, CancellationToken cancellationToken)
    {
        if (mask > (1 << Vm204Api.RelayCount) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, "The VM204 has relays 1–4 only.");
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        if (mask == 0)
        {
            return;
        }

        // A poll under way gives way; the board's answer, its status right after the command, is the confirmation.
        try
        {
            await _poller.RunCommandAsync(
                async token =>
                {
                    if (Volatile.Read(ref _key) is not { } key)
                    {
                        // Logged out (a refused key, a new login): the poll the poller asks for at once logs in again.
                        throw new BoardNotConnectedException("The VM204 is logging in again; the command was not sent.");
                    }

                    // Every failure is thrown as it is, so the poller counts it as a failed poll, as it would a poll's.
                    // A refused key is dropped: the poll the poller asks for at once logs in again.
                    var answer = await SendAsync($"{path}?key={key}&relay={Vm204TraceText.RelayParameter(mask)}", routine: false, token)
                        .ConfigureAwait(false);
                    return ReadStatusAnswer(answer, key);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the board at once.
            throw new BoardNotConnectedException("The VM204 didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (ex is Vm204AccessDeniedException or Vm204ProtocolException)
        {
            // The board answered, but not with a status: counted and read in the same way.
            throw new BoardNotConnectedException($"The VM204 didn't take the command: {ex.Message}", ex);
        }
    }

    // A request that got no answer at all.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or HttpRequestException or IOException;
    }

    // Called one at a time, by the poller. Returns the key.
    private async Task<string> LogInAsync(CancellationToken cancellationToken)
    {
        var login = Login;
        var answer = await SendAsync(
            $"{Vm204Api.AuthPath}?user={login.Username}&password={login.Password}",
            routine: false,
            cancellationToken,
            hideAnswer: true).ConfigureAwait(false);
        if (answer.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.AccessDenied();
            throw new Vm204AccessDeniedException();
        }

        EnsureOk(answer);
        var key = Vm204Json.ReadKey(answer.Body);
        Volatile.Write(ref _key, key);
        var endpoint = Endpoint;
        _logger.LoggedIn(endpoint.Host, endpoint.Port);
        return key;
    }

    private Vm204Status ReadStatusAnswer(Answer answer, string key)
    {
        if (answer.StatusCode == HttpStatusCode.BadRequest)
        {
            Interlocked.CompareExchange(ref _key, null, key);
            throw new Vm204AccessDeniedException("The VM204 refused the key; the app logs in again.");
        }

        EnsureOk(answer);
        var status = Vm204Json.ReadStatus(answer.Body);
        var routine = status.SameSwitches(Interlocked.Exchange(ref _lastTraced, status));
        Trace(BoardTraceDirection.Received, Vm204TraceText.Status(status), answer.Text, isRoutine: routine);
        return status;
    }

    private static void EnsureOk(Answer answer)
    {
        if (answer.StatusCode != HttpStatusCode.OK)
        {
            throw new Vm204ProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"The VM204 answered {(int)answer.StatusCode}."));
        }
    }

    /// <summary>
    /// One GET to the board, with a timeout, and its answer read up to <see cref="Vm204Api.MaxResponseBytes"/>. Traces
    /// the request; a 200 status answer is traced by the caller, which knows what it says.
    /// </summary>
    private async Task<Answer> SendAsync(string pathAndQuery, bool routine, CancellationToken cancellationToken, bool hideAnswer = false)
    {
        var shown = $"GET {Vm204TraceText.Redact(pathAndQuery)}";
        Trace(BoardTraceDirection.Sent, Vm204TraceText.Request(pathAndQuery), shown, isRoutine: routine);
        try
        {
            return await SendCoreAsync(pathAndQuery, hideAnswer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or Vm204ProtocolException or UriFormatException)
        {
            // One failed poll doesn't lose the connection, so it shows here; the reason never holds the query.
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            throw;
        }
    }

    private async Task<Answer> SendCoreAsync(string pathAndQuery, bool hideAnswer, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{Endpoint}{pathAndQuery}"))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.ConnectionClose = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > Vm204Api.MaxResponseBytes)
            {
                throw new Vm204ProtocolException($"The VM204's answer is longer than {Vm204Api.MaxResponseBytes} bytes.");
            }

            var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            // A login's answer never shows: a 200 holds the key, and whatever else came back might repeat the password.
            var text = hideAnswer && response.StatusCode != HttpStatusCode.OK
                ? Vm204TraceText.Answer((int)response.StatusCode, null)
                : Vm204TraceText.Answer((int)response.StatusCode, Encoding.UTF8.GetString(body), hideAnswer);
            var answer = new Answer(response.StatusCode, body, text);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Trace(BoardTraceDirection.Received, Refusal(pathAndQuery, response.StatusCode), text, isError: true);
            }
            else if (hideAnswer)
            {
                Trace(BoardTraceDirection.Received, "Logged in", text);
            }

            return answer;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The VM204 did not answer within {RequestTimeout.TotalSeconds} s.");
        }
    }

    private static string Refusal(string pathAndQuery, HttpStatusCode statusCode)
    {
        if (statusCode != HttpStatusCode.BadRequest)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The board answered {(int)statusCode}");
        }

        return pathAndQuery.StartsWith(Vm204Api.AuthPath, StringComparison.Ordinal)
            ? "Wrong user name or password"
            : "The board refused the key";
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[Vm204Api.MaxResponseBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > Vm204Api.MaxResponseBytes)
            {
                throw new Vm204ProtocolException($"The VM204's answer is longer than {Vm204Api.MaxResponseBytes} bytes.");
            }

            return buffer[..total];
        }
    }

    // The first status after a new connection always gets a toast.
    private void ForgetLastStatus()
    {
        Volatile.Write(ref _lastTraced, null);
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false)
    {
        if (Traced is not { } handlers)
        {
            return;
        }

        var entry = new BoardTraceEntry(_timeProvider.GetUtcNow(), direction, text, bytes, isError, isRoutine);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardTraceEntry>>())
        {
            try
            {
                handler(this, entry);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    private sealed record Answer(HttpStatusCode StatusCode, byte[] Body, string Text);
}
