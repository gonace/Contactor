using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Velleman;

/// <summary>
/// Talks to a VM208 over its web API (<see cref="Vm208Api"/>): polls the status through
/// <see cref="BoardPoller{TReading}"/> and switches relays, one request per relay, as the firmware takes them. One
/// request at a time (the poller sees to it). Commands are never queued: without a connection they fail at once with
/// <see cref="BoardNotConnectedException"/>, a poll under way gives way to them, and the board's answer to one, its
/// status right after, is reported at once. The API has no login, so there is none to send.
/// The board's own rules are the relays' week schedules on the board (<see cref="Vm208Alarm"/>): the client reads them,
/// one <c>GET /getalarms</c> per relay, at the first poll of each connection and then every
/// <see cref="SchedulesInterval"/>, and never writes them on its own: only <see cref="TurnOffOwnRulesAsync"/>, on a
/// person's press, turns one relay's schedule off with <c>POST /shedule_set</c>. A pulse or a timer started on the
/// board's own page shows in every status (<see cref="Vm208Status.Running"/>) and is a rule too, which the app can't turn
/// off (<see cref="OwnRules.CantTurnOff"/>): it never sends <c>/stoppulse</c> or <c>/stoptimer</c>, which toggle the relay.
/// </summary>
public sealed class Vm208Client : IPolledBoard<Vm208Status>, IDisposable, IAsyncDisposable
{
    /// <summary>How long a request may take before it counts as unanswered.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the relays' schedules are read again after the read at connecting. The VM208 has no revision counter
    /// that says a schedule changed, so every check costs one request per relay on a board that takes one request per
    /// connection and is already polled every few seconds; once a minute adds 4 requests to about 30 polls, as the
    /// Waveshare's modes and the Shelly's settings are read. A schedule someone sets on the board's own page shows
    /// within a minute, and whatever it switches shows at the next poll anyway.
    /// </summary>
    public static readonly TimeSpan SchedulesInterval = TimeSpan.FromSeconds(60);

    private const int MaxResponseHeadersKilobytes = 8;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm208Client> _logger;
    private readonly HttpClient _http;
    private readonly BoardPoller<Vm208Status> _poller;

    private BoardEndpoint _endpoint;
    private Vm208Status? _lastTraced;
    private Vm208Status? _lastReported;
    private int _wasConnected;

    // Under the poller's lock: each relay's schedule as last read (null while unknown), whether it is read on this
    // connection, when, and whether the last read failed.
    private IReadOnlyList<Vm208Alarm>[]? _schedules;
    private bool _schedulesRead;
    private bool _schedulesFailed;
    private DateTimeOffset _schedulesReadUtc;

    // Under the poller's lock: the relays with a pulse or a timer running, as the last status said; null while unknown.
    private byte? _running;

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

    /// <summary>
    /// Raised when only the board's own rules changed (<see cref="Vm208State.OwnRules"/>), not the relays, the input or
    /// the connection, which come as <see cref="StateChanged"/>. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<Vm208State>? OwnRulesChanged;

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

        Vm208Log.EndpointChanged(_logger, endpoint.Host, endpoint.Port);

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

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/>, as a person asked: for each relay's <see cref="OwnRuleKind.Timer"/>
    /// rule, its schedule is read (<c>GET /getalarms</c>), sent back with every alarm at its own time and disabled in one
    /// <c>POST /shedule_set</c>, which the board saves to its flash, and read again. Never <c>/setalarm</c>, which the
    /// board forgets at a restart, and never <c>/stoptimer</c> or <c>/stoppulse</c>, which toggle the relay. The others
    /// still go when one relay's fails. A rule given twice is taken once. A relay with a pulse or a timer running, as the
    /// last status said, is left (<see cref="OwnRules.CantTurnOff"/>): nothing is sent for it, and it isn't taken. Returns
    /// the rules whose schedule the board took (answered 200), in relay order. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a relay above 4 or another kind of rule, and <see cref="BoardNotConnectedException"/> without a connection, both before anything
    /// is sent; and <see cref="BoardNotConnectedException"/> when the board took none of them or didn't answer.
    /// </summary>
    public async Task<IReadOnlyList<OwnRule>> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        List<OwnRule> asked = [];
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("A rule can't be null.", nameof(rules));
            }

            if (rule.Kind != OwnRuleKind.Timer)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "A VM208's own rules are its relays' schedules.");
            }

            if (rule.Relay > Vm208Api.RelayCount)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, "The VM208 has relays 1–4 only.");
            }

            if (!asked.Contains(rule))
            {
                asked.Add(rule);
            }
        }

        if (asked.Count == 0)
        {
            return [];
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        try
        {
            var taken = new StrongBox<IReadOnlyList<OwnRule>>([]);
            await _poller.RunSwitchesAsync(token => TurnOffSchedulesAndReadAsync([.. asked.OrderBy(r => r.Relay)], taken, token), cancellationToken)
                .ConfigureAwait(false);
            return taken.Value ?? [];
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the board at once, schedules included.
            throw new BoardNotConnectedException("The VM208 didn't answer; its schedules may not have been turned off.", ex);
        }
        catch (Exception ex) when (ex is Vm208ProtocolException or BoardRefusedException)
        {
            throw new BoardNotConnectedException($"The VM208 didn't turn its schedules off: {ex.Message}", ex);
        }
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

    /// <summary>
    /// Disposes the HTTP client once the poll or command under way has ended (at most the poller's
    /// <see cref="BoardPoller{TReading}.DisposeWait"/>): nothing new starts, and no request is still using it. A command
    /// that outlasts the wait throws <see cref="BoardNotConnectedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _poller.DisposeAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        // The poller first, so no poll or command starts on the HTTP client being disposed, and one under way that it
        // cuts off ends as BoardNotConnectedException, uncounted.
        _poller.Dispose();
        _http.Dispose();
    }

    // Nothing to open: the board takes each request on its own connection and has no login. The first answered poll
    // connects, and reads the schedules: another board may answer now, or the same one after a restart.
    Task IPolledBoard<Vm208Status>.ConnectAsync(CancellationToken cancellationToken)
    {
        _schedules = null;
        _schedulesRead = false;
        _schedulesFailed = false;
        _running = null;
        return Task.CompletedTask;
    }

    async Task<Vm208Status> IPolledBoard<Vm208Status>.ReadAsync(CancellationToken cancellationToken)
    {
        var target = Endpoint;
        var status = await ExchangeStatusAsync(HttpMethod.Get, $"{Vm208Api.StatusPath}?{Vm208Api.StatusQuery}", form: null, routine: true, target, command: false, cancellationToken)
            .ConfigureAwait(false);

        // Only once the board answered its status: the schedules never decide whether it is there.
        if (!_schedulesRead || _timeProvider.GetUtcNow() - _schedulesReadUtc >= SchedulesInterval)
        {
            await ReadSchedulesAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return status with { OwnRules = CurrentOwnRules(status.Running) };
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
            var answer = await SendAsync(method, pathAndQuery, form, routine, quietErrors: false, cancellationToken).ConfigureAwait(false);
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
        Volatile.Write(ref _lastReported, state.Reading);
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            Vm208Log.Connected(_logger, endpoint.Host, endpoint.Port);
        }

        Raise(StateChanged, state);
    }

    // The comparer leaves out the transistor outputs and the own rules: tell only a change of the rules.
    void IPolledBoard<Vm208Status>.OnReadingChanged(PolledState<Vm208Status> state)
    {
        var before = Interlocked.Exchange(ref _lastReported, state.Reading);
        if (before is null || before.OwnRules != state.Reading?.OwnRules)
        {
            Raise(OwnRulesChanged, state);
        }
    }

    private void Raise(EventHandler<Vm208State>? handlers, PolledState<Vm208Status> state)
    {
        if (handlers is null)
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
                Vm208Log.SubscriberFailed(_logger, ex);
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
                Vm208Log.ConnectionLost(_logger, detail);
                Trace(BoardTraceDirection.None, $"Connection lost: {detail}", isError: true);
                break;
            case BoardPollStep.Waiting:
                Vm208Log.Reconnecting(_logger, detail);
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

    // Under the poller's lock: every relay's schedule, one GET /getalarms each, relays 1–4 only. Never fails the poll:
    // an answer that isn't 200 or can't be read, or no answer, makes the schedules unknown (and stops the read: the
    // board wouldn't answer the next relay either), and the read is tried again after SchedulesInterval. Only a
    // cancelled poll (a command takes its place, or the app stops) ends it without a result.
    private async Task ReadSchedulesAsync(BoardEndpoint target, CancellationToken cancellationToken)
    {
        var before = _schedules;
        var schedules = new IReadOnlyList<Vm208Alarm>[Vm208Api.RelayCount];
        var failed = false;
        for (var relay = 1; relay <= Vm208Api.RelayCount; relay++)
        {
            try
            {
                // Never to another board: the poll that began it was for this address.
                EnsureSameBoard(target, command: false);
                schedules[relay - 1] = await GetScheduleAsync(relay, routine: true, before?[relay - 1], quietErrors: _schedulesFailed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNoAnswer(ex) || ex is Vm208ProtocolException or UriFormatException or BoardNotConnectedException)
            {
                // Traced by SendAsync or GetScheduleAsync; this says what it means. A toast only the first time: while the
                // reads keep failing (a firmware without /getalarms answers 404 every minute), every trace is routine.
                Trace(BoardTraceDirection.None, $"The board's schedules can't be read: {ex.Message}", isError: true, isRoutine: _schedulesFailed);
                failed = true;
                break;
            }
        }

        _schedules = failed ? null : schedules;
        _schedulesFailed = failed;
        _schedulesReadUtc = _timeProvider.GetUtcNow();
        _schedulesRead = true;
    }

    // One relay's schedule, relays 1–4 only (Vm208Api.AlarmsQuery): GET /getalarms?channel=N. Throws as SendAsync, and
    // Vm208ProtocolException for an answer that isn't 200 or isn't a schedule. A toast only when it differs from before,
    // and for a failure only unless quietErrors (one that already showed).
    private async Task<IReadOnlyList<Vm208Alarm>> GetScheduleAsync(
        int relay,
        bool routine,
        IReadOnlyList<Vm208Alarm>? before,
        bool quietErrors,
        CancellationToken cancellationToken)
    {
        var answer = await SendAsync(HttpMethod.Get, $"{Vm208Api.AlarmsPath}?{Vm208Api.AlarmsQuery(relay)}", form: null, routine, quietErrors, cancellationToken).ConfigureAwait(false);
        if (answer.StatusCode != HttpStatusCode.OK)
        {
            throw new Vm208ProtocolException(string.Create(CultureInfo.InvariantCulture, $"The VM208 answered {(int)answer.StatusCode}."));
        }

        IReadOnlyList<Vm208Alarm> alarms;
        try
        {
            alarms = Vm208Json.ReadAlarms(answer.Body);
        }
        catch (Vm208ProtocolException ex)
        {
            Trace(BoardTraceDirection.Received, ex.Message, answer.Text, isError: true, isRoutine: quietErrors);
            throw;
        }

        var unchanged = before is not null && before.SequenceEqual(alarms);
        Trace(BoardTraceDirection.Received, Vm208TraceText.Schedule(relay, alarms), answer.Text, isRoutine: routine && unchanged);
        return alarms;
    }

    // Under the poller's lock: the own rules, or null while the schedules or the running pulses and timers are unknown.
    // A Timer rule on each relay whose schedule has an enabled alarm or that has a pulse or a timer running, in relay
    // order; each relay is its own unit, so turning one off takes no other rule with it. A relay with a pulse or a timer
    // running can't be turned off: the app never stops one, and turning its schedule off wouldn't remove the rule.
    private OwnRules? CurrentOwnRules(byte? running)
    {
        if (_schedules is not { } schedules || running is not { } runs)
        {
            return null;
        }

        List<OwnRule> rules = [];
        List<OwnRule> cantTurnOff = [];
        for (var relay = 1; relay <= schedules.Length; relay++)
        {
            var isRunning = (runs & (1 << (relay - 1))) != 0;
            if (schedules[relay - 1].Any(a => a.Enabled) || isRunning)
            {
                var rule = new OwnRule(relay, OwnRuleKind.Timer);
                rules.Add(rule);
                if (isRunning)
                {
                    cantTurnOff.Add(rule);
                }
            }
        }

        return rules.Count == 0
            ? Contactor.Boards.OwnRules.Empty
            : new OwnRules(rules, rules.Select(r => (IReadOnlyCollection<OwnRule>)[r]), cantTurnOff);
    }

    // Under the poller's lock: for each relay asked, in relay order, its schedule read fresh (so each alarm keeps the
    // time the board has now), one POST /shedule_set with every alarm disabled, and the schedule read back. Then the
    // status, which the poller takes as a poll's answer. A relay whose read or save isn't answered 200 or can't be read
    // is left, and the others still go; no answer at all stops the rest.
    private async Task<CommandReading<Vm208Status>> TurnOffSchedulesAndReadAsync(
        IReadOnlyList<OwnRule> asked,
        StrongBox<IReadOnlyList<OwnRule>> taken,
        CancellationToken cancellationToken)
    {
        var target = Endpoint;
        List<OwnRule> done = [];
        ExceptionDispatchInfo? noAnswer = null;
        ExceptionDispatchInfo? unreadable = null;
        string? refusal = null;
        foreach (var rule in asked)
        {
            var relay = rule.Relay;
            if (_running is { } running && (running & (1 << (relay - 1))) != 0)
            {
                // A pulse or a timer runs on it: the rule stays whatever the schedule, so nothing is sent for it.
                refusal ??= string.Create(CultureInfo.InvariantCulture, $"relay {relay}: a pulse or a timer started on the board runs on it");
                continue;
            }

            var posted = false;
            try
            {
                // Only to the board the command began with, while it counts as connected.
                EnsureSameBoard(target, command: true);
                var alarms = await GetScheduleAsync(relay, routine: false, before: null, quietErrors: false, cancellationToken).ConfigureAwait(false);
                EnsureSameBoard(target, command: true);
                var form = Vm208Api.ScheduleForm(relay, [.. alarms.Select(a => a with { Enabled = false })]);
                posted = true;
                var answer = await SendAsync(HttpMethod.Post, Vm208Api.ScheduleSetPath, form, routine: false, quietErrors: false, cancellationToken).ConfigureAwait(false);
                if (answer.StatusCode != HttpStatusCode.OK)
                {
                    // Traced by SendCoreAsync with the board's answer: a 400 has no body.
                    refusal ??= string.Create(CultureInfo.InvariantCulture, $"relay {relay}: the VM208 answered {(int)answer.StatusCode}");
                    continue;
                }

                // The answer is the board's settings: not the app's to show.
                Trace(
                    BoardTraceDirection.Received,
                    string.Create(CultureInfo.InvariantCulture, $"The board saved relay {relay}'s schedule"),
                    Vm208TraceText.SettingsAnswer((int)answer.StatusCode));
                done.Add(rule);
            }
            catch (Vm208ProtocolException ex)
            {
                // A save whose answer can't be read may have been made: the next poll reads every schedule again.
                _schedulesRead &= !posted;
                refusal ??= string.Create(CultureInfo.InvariantCulture, $"relay {relay}: {ex.Message}");
                unreadable ??= ExceptionDispatchInfo.Capture(ex);
                Trace(BoardTraceDirection.None, $"The VM208 didn't take the change for relay {relay}'s schedule: {ex.Message}", isError: true);
                continue;
            }
            catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException || (ex is BoardNotConnectedException && done.Count > 0))
            {
                // No answer, or (after a relay was taken) the board moved or was lost: nothing more goes. A save without
                // an answer may have been made: the next poll reads every schedule again.
                _schedulesRead &= !posted;
                noAnswer = ExceptionDispatchInfo.Capture(ex);
                break;
            }

            // Read back: the next state confirms it. A read that fails makes the schedules unknown until the next read.
            try
            {
                EnsureSameBoard(target, command: true);
                var after = await GetScheduleAsync(relay, routine: false, before: null, quietErrors: false, cancellationToken).ConfigureAwait(false);
                if (_schedules is { } known)
                {
                    var schedules = (IReadOnlyList<Vm208Alarm>[])known.Clone();
                    schedules[relay - 1] = after;
                    _schedules = schedules;
                }
            }
            catch (Exception ex) when (ex is Vm208ProtocolException or BoardNotConnectedException || IsNoAnswer(ex) || ex is UriFormatException)
            {
                _schedules = null;
                _schedulesRead = false;
                if (ex is not Vm208ProtocolException)
                {
                    noAnswer = ExceptionDispatchInfo.Capture(ex);
                    break;
                }
            }
        }

        // Whatever came of it, a schedule not read back is read again at the next poll.
        if (_schedules is null)
        {
            _schedulesRead = false;
        }

        if (done.Count == 0)
        {
            noAnswer?.Throw();
            unreadable?.Throw();
            throw new BoardRefusedException($"The VM208 didn't take the change: {refusal}");
        }

        taken.Value = done;
        if (noAnswer is not null || Endpoint != target)
        {
            // The changes went out; the poller keeps the last state and reads again at once.
            return default;
        }

        try
        {
            var status = await ExchangeStatusAsync(HttpMethod.Get, $"{Vm208Api.StatusPath}?{Vm208Api.StatusQuery}", form: null, routine: false, target, command: true, cancellationToken)
                .ConfigureAwait(false);
            return new CommandReading<Vm208Status>(status);
        }
        catch (Exception ex) when (ex is Vm208ProtocolException or BoardNotConnectedException || IsNoAnswer(ex) || ex is UriFormatException)
        {
            return default;
        }
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

        var before = Interlocked.Exchange(ref _lastTraced, status);
        var routine = status.SameSwitches(before) && status.Pulses == before?.Pulses && status.Timers == before?.Timers;
        Trace(BoardTraceDirection.Received, Vm208TraceText.Status(status), answer.Text, isRoutine: routine);

        // A command's answer is taken as a poll's: with the schedules as last read, so they don't seem to change, and the
        // pulses and timers it shows.
        _running = status.Running;
        return status with { OwnRules = CurrentOwnRules(status.Running) };
    }

    /// <summary>
    /// One request to the board, with a timeout, and its answer read up to <see cref="Vm208Api.MaxResponseBytes"/>.
    /// Traces the request and an answer other than 200; a 200 is traced by the caller, which knows what it says. With
    /// <paramref name="quietErrors"/> a failure is traced as routine, without a toast: one that already showed, and
    /// shows again only after a read that worked.
    /// </summary>
    private async Task<Answer> SendAsync(HttpMethod method, string pathAndQuery, string? form, bool routine, bool quietErrors, CancellationToken cancellationToken)
    {
        var parts = pathAndQuery.Split('?', 2);
        var path = parts[0];
        Trace(BoardTraceDirection.Sent, Vm208TraceText.Request(method.Method, path, form, parts.Length > 1 ? parts[1] : null), Vm208TraceText.RequestBytes(method.Method, pathAndQuery, form), isRoutine: routine);
        try
        {
            return await SendCoreAsync(method, pathAndQuery, form, quietErrors, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or UriFormatException)
        {
            // One failed poll doesn't lose the connection, so it shows here.
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true, isRoutine: quietErrors);
            throw;
        }
        catch (Vm208ProtocolException ex)
        {
            // An answer too long to be a VM208's: it answered, but nothing of it is read.
            Trace(BoardTraceDirection.Received, $"Answer too long: {ex.Message}", isError: true, isRoutine: quietErrors);
            throw;
        }
    }

    private async Task<Answer> SendCoreAsync(HttpMethod method, string pathAndQuery, string? form, bool quietErrors, CancellationToken cancellationToken)
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
                    isError: true,
                    isRoutine: quietErrors);
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
                Vm208Log.SubscriberFailed(_logger, ex);
            }
        }
    }

    private sealed record Answer(HttpStatusCode StatusCode, byte[] Body, string Text);
}
