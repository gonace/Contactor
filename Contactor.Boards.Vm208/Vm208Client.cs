using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Vm208;

/// <summary>
/// Talks to a VM208 over its web API (<see cref="Vm208Api"/>): polls the status through
/// <see cref="BoardPoller{TReading}"/> and switches relays, one request per relay, as the firmware takes them. One
/// request at a time (the poller sees to it). Commands are never queued: without a connection they fail at once with
/// <see cref="BoardNotConnectedException"/>, a poll under way gives way to them, and the board's answer to one, its
/// status right after, is reported at once. The API has no login, so there is none to send.
/// </summary>
public sealed class Vm208Client : IPolledBoard<Vm208Status>, IDisposable
{
    /// <summary>How long a request may take before it counts as unanswered.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private const int MaxResponseHeadersKilobytes = 8;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm208Client> _logger;
    private readonly HttpClient _http;
    private readonly BoardPoller<Vm208Status> _poller;

    private BoardEndpoint _endpoint;
    private Vm208Status? _lastTraced;
    private int _wasConnected;

    public Vm208Client(IOptions<Vm208Options> options, TimeProvider timeProvider, ILogger<Vm208Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("Vm208:PollSeconds must be at least 1.", nameof(options));
        }

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
            // The requests time out with _timeProvider instead, see SendCoreAsync.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _poller = new BoardPoller<Vm208Status>(this, timeProvider, Vm208Status.SwitchesComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays or the input change, and when the connection is lost or comes back; not on a poll that
    /// changed nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<Vm208State>? StateChanged;

    /// <summary>Every request and answer and every connection step, in plain words. Handlers must be quick.</summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public Vm208State State
    {
        get
        {
            var state = _poller.State;
            return new Vm208State(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client connects: <c>Vm208:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>Moves to another address at once; nothing happens if it is the same.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        _logger.EndpointChanged(endpoint.Host, endpoint.Port);

        // Another board may be at the new address: nothing is known about it until it answers.
        _poller.Forget();
        _poller.Restart();
    }

    /// <summary>Polls until cancelled. Never throws because of the board. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1, relays 1–4), one request each.</summary>
    public Task<byte> TurnOnAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, turnOn: true, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1, relays 1–4), one request each.</summary>
    public Task<byte> TurnOffAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, turnOn: false, cancellationToken);
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

    // Nothing to open: the board takes each request on its own connection and has no login. The first answered poll
    // connects.
    Task IPolledBoard<Vm208Status>.ConnectAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    Task<Vm208Status> IPolledBoard<Vm208Status>.ReadAsync(CancellationToken cancellationToken)
    {
        return ExchangeStatusAsync(HttpMethod.Get, $"{Vm208Api.StatusPath}?{Vm208Api.StatusQuery}", form: null, routine: true, Endpoint, command: false, cancellationToken);
    }

    /// <summary>
    /// A request whose answer is the board's status. A busy answer is sent again, up to <see cref="Vm208Api.BusyRetries"/>
    /// times, <see cref="Vm208Api.BusyWait"/> apart; only then does it count, as any status the app can't read. Never
    /// sent again to another board: not after the address moved from <paramref name="target"/>, and for a
    /// <paramref name="command"/> not once the board counts as lost (<see cref="EnsureSameBoard"/>).
    /// </summary>
    private async Task<Vm208Status> ExchangeStatusAsync(
        HttpMethod method,
        string pathAndQuery,
        string? form,
        bool routine,
        BoardEndpoint target,
        bool command,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var answer = await SendAsync(method, pathAndQuery, form, routine, cancellationToken).ConfigureAwait(false);
            var willRetry = attempt < Vm208Api.BusyRetries;
            try
            {
                return ReadStatusAnswer(answer, willRetryBusy: willRetry);
            }
            catch (Vm208ProtocolException ex) when (ex.IsBusy && willRetry)
            {
                Trace(BoardTraceDirection.None, string.Create(CultureInfo.InvariantCulture, $"Busy: asking again ({attempt + 1} of {Vm208Api.BusyRetries})"), null);
                await Task.Delay(Vm208Api.BusyWait, _timeProvider, cancellationToken).ConfigureAwait(false);
                EnsureSameBoard(target, command);
            }
        }
    }

    /// <summary>
    /// Throws <see cref="BoardNotConnectedException"/> when the address moved from <paramref name="target"/> (another
    /// board may be there now), or, for a <paramref name="command"/>, when the board counts as lost: nothing more is sent
    /// then. A poll may still be connecting, so for a poll only the address counts.
    /// </summary>
    private void EnsureSameBoard(BoardEndpoint target, bool command)
    {
        if (Endpoint != target || (command && !_poller.State.IsConnected))
        {
            throw new BoardNotConnectedException("The VM208's address changed, or it was lost: nothing more was sent.");
        }
    }

    void IPolledBoard<Vm208Status>.OnStateChanged(PolledState<Vm208Status> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        if (StateChanged is not { } handlers)
        {
            return;
        }

        var vm208State = new Vm208State(state.IsConnected, state.Reading, state.LastContactUtc);

        // Each subscriber on its own: one that fails must not keep the state from the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<Vm208State>>())
        {
            try
            {
                handler(this, vm208State);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    void IPolledBoard<Vm208Status>.OnStep(BoardPollStep kind, string? detail)
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

    private async Task<byte> CommandAsync(byte mask, bool turnOn, CancellationToken cancellationToken)
    {
        if (mask > (1 << Vm208Api.RelayCount) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, "The VM208 has relays 1–4 only.");
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        if (mask == 0)
        {
            return 0;
        }

        // A poll under way gives way. The firmware switches one relay per request, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // Only a request that gets no answer at all (the board is gone) stops it.
        try
        {
            var taken = new StrongBox<byte>();
            await _poller.RunCommandAsync(token => SwitchEachAsync(mask, turnOn, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The VM208 didn't answer; the command may not have been carried out.", ex);
        }
        catch (Vm208ProtocolException ex)
        {
            // No relay's answer could be read: counted and read in the same way.
            throw new BoardNotConnectedException($"The VM208 didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the last status the board answered with, which the poller takes as a poll's, and
    // puts the relays whose answer it could read in <paramref name="taken"/>.
    private async Task<Vm208Status> SwitchEachAsync(byte mask, bool turnOn, StrongBox<byte> taken, CancellationToken cancellationToken)
    {
        Vm208Status? status = null;
        string? refusal = null;
        var target = Endpoint;
        for (var relay = 1; relay <= Vm208Api.RelayCount; relay++)
        {
            if ((mask & (1 << (relay - 1))) == 0)
            {
                continue;
            }

            // The rest of the mask goes only to the board the command began with, while it counts as connected.
            EnsureSameBoard(target, command: true);

            // No answer (IsNoAnswer) is thrown as it is and stops the rest: the poller counts it as a failed poll.
            try
            {
                status = await ExchangeStatusAsync(HttpMethod.Post, Vm208Api.RelayPath, Vm208Api.RelayForm(relay, turnOn), routine: false, target, command: true, cancellationToken)
                    .ConfigureAwait(false);
                taken.Value |= (byte)(1 << (relay - 1));
            }
            catch (Vm208ProtocolException ex)
            {
                // The board answered, but not with a status the app can read (an error, too busy, too long): the
                // relay may or may not have switched. The others still go; a poll straight after shows the truth.
                refusal ??= ex.Message;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The VM208 didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
        }

        if (refusal is null)
        {
            return status!;
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller counts it as a failed poll, as it would a poll whose answer it can't read,
        // and reads the board at once, which shows what really happened.
        if (status is null)
        {
            throw new Vm208ProtocolException(refusal);
        }

        // Some were: the last readable answer is the state for now, and a read at once confirms the rest. The command
        // doesn't fail (what was taken counts as switched), so the poller's read after a failed command doesn't come:
        // ask for it here.
        _poller.PollNow();
        return status;
    }

    // A request that got no answer at all.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or HttpRequestException or IOException;
    }

    // A busy answer that will be asked again isn't an error yet: it is traced plainly.
    private Vm208Status ReadStatusAnswer(Answer answer, bool willRetryBusy = false)
    {
        if (answer.StatusCode != HttpStatusCode.OK)
        {
            throw new Vm208ProtocolException(string.Create(
                CultureInfo.InvariantCulture,
                $"The VM208 answered {(int)answer.StatusCode}."));
        }

        Vm208Status status;
        try
        {
            status = Vm208Json.ReadStatus(answer.Body);
        }
        catch (Vm208ProtocolException ex)
        {
            Trace(BoardTraceDirection.Received, ex.Message, answer.Text, isError: !(ex.IsBusy && willRetryBusy));
            throw;
        }

        var routine = status.SameSwitches(Interlocked.Exchange(ref _lastTraced, status));
        Trace(BoardTraceDirection.Received, Vm208TraceText.Status(status), answer.Text, isRoutine: routine);
        return status;
    }

    /// <summary>
    /// One request to the board, with a timeout, and its answer read up to <see cref="Vm208Api.MaxResponseBytes"/>.
    /// Traces the request and an answer other than 200; a 200 is traced by the caller, which knows what it says.
    /// </summary>
    private async Task<Answer> SendAsync(HttpMethod method, string pathAndQuery, string? form, bool routine, CancellationToken cancellationToken)
    {
        var path = pathAndQuery.Split('?', 2)[0];
        Trace(BoardTraceDirection.Sent, Vm208TraceText.Request(method.Method, path, form), Vm208TraceText.RequestBytes(method.Method, pathAndQuery, form), isRoutine: routine);
        try
        {
            return await SendCoreAsync(method, pathAndQuery, form, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or UriFormatException)
        {
            // One failed poll doesn't lose the connection, so it shows here.
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            throw;
        }
        catch (Vm208ProtocolException ex)
        {
            // An answer too long to be a VM208's: it answered, but nothing of it is read.
            Trace(BoardTraceDirection.Received, $"Answer too long: {ex.Message}", isError: true);
            throw;
        }
    }

    private async Task<Answer> SendCoreAsync(HttpMethod method, string pathAndQuery, string? form, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri($"http://{Endpoint}{pathAndQuery}"))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.ConnectionClose = true;
            if (form is not null)
            {
                // As the board's own page sends it; the firmware reads form parameters only from this content type.
                request.Content = new StringContent(form, Encoding.ASCII);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > Vm208Api.MaxResponseBytes)
            {
                throw new Vm208ProtocolException($"The VM208's answer is longer than {Vm208Api.MaxResponseBytes} bytes.");
            }

            var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            var text = Vm208TraceText.Answer((int)response.StatusCode, Encoding.UTF8.GetString(body));
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Trace(
                    BoardTraceDirection.Received,
                    string.Create(CultureInfo.InvariantCulture, $"The board answered {(int)response.StatusCode}"),
                    text,
                    isError: true);
            }

            return new Answer(response.StatusCode, body, text);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The VM208 did not answer within {RequestTimeout.TotalSeconds} s.");
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[Vm208Api.MaxResponseBytes + 1];
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

            if (total > Vm208Api.MaxResponseBytes)
            {
                throw new Vm208ProtocolException($"The VM208's answer is longer than {Vm208Api.MaxResponseBytes} bytes.");
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
