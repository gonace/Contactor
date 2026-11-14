using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Schedules;

/// <summary>
/// Runs the schedules: every minute, on the minute, it turns relays on at their OnTime and off at their OffTime,
/// logged with Actor <c>schedule</c>. A relay it turns on is marked as turned on by the schedule and gets no
/// auto-off; the schedule's OffTime is its end. Overlapping schedules: ON wins, so an OFF is skipped while another
/// of the relay's schedules is still running.
/// <para>
/// It remembers the last minute it handled (<see cref="BoardRuntime.LastTickUtc"/>). After a restart or a lost connection it
/// catches up on the last 24 h: a run that is still going is turned on (this is also the reconciliation the spec
/// asks for at start and reconnect), and a missed OFF is only applied to a relay the schedule itself turned on, so
/// nothing a person turned on is ever turned off at start.
/// </para>
/// <para>
/// A relay it turned on is also turned off when no running schedule covers it any more (its schedule was deleted,
/// paused or moved during the run), since nothing else would. A relay a person turned on keeps its own auto-off even
/// inside a schedule window: their choice wins for that run, and the schedule runs again at its next OnTime.
/// </para>
/// <para>
/// A group's schedules follow the same rules, but start and stop the group through <see cref="GroupSequencer"/>: in
/// its start order, and with its run-on times at the OffTime. The sequencer logs those as the group's own events.
/// </para>
/// </summary>
public sealed partial class SchedulerService : BackgroundService
{
    public const string Actor = EventActors.Schedule;

    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    // An OFF this late is a catch-up, not the minute's own work.
    private static readonly TimeSpan OnTimeTolerance = TimeSpan.FromMinutes(1);

    private readonly OnlyBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly GroupSequencer _groups;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SchedulerService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    // Every (re)connect bumps the generation; a tick reconciles when it hasn't handled the current one. A counter, not a
    // flag, so a reconnect during a tick isn't wiped out when that tick finishes.
    private int _connectGeneration = 1;
    private int _reconciledGeneration;
    private bool _wasConnected;

    // Groups whose start (true) or stop (false) failed for another reason than a lost board: the next tick tries it
    // again, as it would for a relay. Only touched under the tick lock.
    private readonly Dictionary<int, bool> _retryGroups = [];

    public SchedulerService(
        BoardRegistry registry,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        GroupSequencer groups,
        ExpectedChanges expected,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<SchedulerService> logger)
    {
        // Until PR 4 (Issue #4): one board.
        _board = registry.Only;
        _dbFactory = dbFactory;
        _clock = clock;
        _groups = groups;
        _expected = expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;

        // From the start, not from ExecuteAsync (a background thread): a reconnect must never go unnoticed.
        _board.StateChanged += OnStateChanged;
    }

    public override void Dispose()
    {
        _board.StateChanged -= OnStateChanged;
        _wake.Dispose();
        _tickGate.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Handles everything due up to the current minute. Does nothing while the board is disconnected, so the
    /// minutes it misses are caught up once it is back.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken);
        try
        {
            var state = _board.State;
            if (!state.IsConnected)
            {
                return;
            }

            var generation = Volatile.Read(ref _connectGeneration);
            var reconcile = generation != _reconciledGeneration;
            var now = FloorToMinute(_clock.UtcNow);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            // The board's runtime row holds the progress; without a board there is nothing to run.
            if (await db.FirstBoardRuntimeAsync(cancellationToken) is not { } progress)
            {
                return;
            }

            // Handles (from, now]; the very first run starts from now, with nothing to catch up.
            var from = progress.LastTickUtc is { } last ? Max(last, now - MaxCatchUp) : now;
            var schedules = await db.Schedules.AsNoTracking().Where(s => s.IsEnabled).ToListAsync(cancellationToken);
            var windows = ScheduleCalendar.Windows(schedules, _clock.Zone, from, now);
            var runtimes = await db.RelayRuntimes.ToDictionaryAsync(r => r.RelayId, cancellationToken);
            var channels = await BoardChannels.LoadAsync(db, cancellationToken);
            var changed = false;
            var interrupted = false;

            // The board's relays in channel order; the board's state and the commands go by the channel.
            for (var channel = 1; channel <= _board.Capabilities.RelayCount; channel++)
            {
                if (channels.RelayAt(channel) is not { } relayId)
                {
                    continue;
                }

                var relayWindows = windows.Where(w => w.RelayId == relayId).ToList();
                var running = relayWindows.Where(w => w.Contains(now)).OrderByDescending(w => w.OffUtc).FirstOrDefault();
                var isOn = state.IsOn(channel);
                runtimes.TryGetValue(relayId, out var runtime);

                if (running is not null)
                {
                    // On outside the app during its schedule's window (e.g. the schedule's own ON reported as failed,
                    // or the board's button just before): it is the schedule's, and goes off at the OffTime, not after
                    // an auto-off of its own.
                    if (isOn && runtime?.TurnedOnBy == EventActors.External)
                    {
                        await RelayRuns.TakeOverForScheduleAsync(db, relayId, cancellationToken);
                        changed = true;
                    }

                    var startedNow = running.OnUtc > from && running.OnUtc <= now;
                    if (!isOn && (startedNow || reconcile))
                    {
                        if (!await SendAsync(channel, on: true, cancellationToken))
                        {
                            interrupted = true;
                            break;
                        }

                        // Since the ON went out, not the minute: a person's OFF is told apart from it by time (RelayRuns.End).
                        await RelayRuns.StartAsync(db, relayId, Actor, _clock.UtcNow, autoOffAt: null, cancellationToken);
                        Log(db, progress.BoardId, now, relayId, EventActions.On, running.ScheduleId);
                        changed = true;
                    }

                    continue;
                }

                if (!isOn)
                {
                    continue;
                }

                var ended = relayWindows.Where(w => w.OffUtc > from && w.OffUtc <= now).OrderByDescending(w => w.OffUtc).FirstOrDefault();
                var turnedOnBySchedule = runtime?.TurnedOnBy == Actor;
                if (ended is null)
                {
                    // On by a schedule that no longer runs it: deleted, paused or moved during its run, or its OFF
                    // is older than the catch-up reaches. Nothing else would ever turn it off (it has no auto-off).
                    if (!turnedOnBySchedule)
                    {
                        continue;
                    }
                }
                else if (now - ended.OffUtc > OnTimeTolerance && !turnedOnBySchedule)
                {
                    // Someone turned it on by hand; a missed OFF must not turn it off.
                    continue;
                }

                var sent = _clock.UtcNow;
                if (!await SendAsync(channel, on: false, cancellationToken))
                {
                    interrupted = true;
                    break;
                }

                await RelayRuns.EndAsync(db, relayId, sent, cancellationToken);

                Log(db, progress.BoardId, now, relayId, EventActions.Off, ended?.ScheduleId);
                changed = true;
            }

            // The relays' runs are saved first: whatever happens to the groups, a relay this tick switched keeps its
            // "turned on by the schedule" and its event.
            await RelayRuns.SaveAsync(db, cancellationToken);
            if (!interrupted)
            {
                var groups = await TickGroupsAsync(db, channels, windows, from, now, reconcile, cancellationToken);
                changed |= groups.Changed;
                interrupted = groups.Interrupted;
            }

            // What was sent before a lost connection is still recorded; the minute itself is handled again once
            // the board is back, and relays already in the right state are left alone then.
            if (!interrupted)
            {
                progress.LastTickUtc = now;
                _reconciledGeneration = generation;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (changed)
            {
                _changes.Notify();
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }

    // The groups' runs, by the same rules as a relay's: a run starts the group, its OFF stops it with its run-on times,
    // and a missed OFF only stops a group the schedule started.
    private async Task<(bool Changed, bool Interrupted)> TickGroupsAsync(
        ContactorDbContext db,
        BoardChannels channels,
        IReadOnlyList<ScheduleWindow> windows,
        DateTime from,
        DateTime now,
        bool reconcile,
        CancellationToken cancellationToken)
    {
        // Read outside the sequencer's lock, so it can be a moment old; Start and Stop look again under the lock.
        var groups = await db.RelayGroups.AsNoTracking().Include(g => g.Members).ToListAsync(cancellationToken);
        var runtimes = await db.GroupRuntimes.AsNoTracking().ToDictionaryAsync(r => r.GroupId, cancellationToken);
        var changed = false;
        foreach (var group in groups)
        {
            var groupWindows = windows.Where(w => w.GroupId == group.Id).ToList();
            runtimes.TryGetValue(group.Id, out var runtime);
            bool? retry = _retryGroups.Remove(group.Id, out var wanted) ? wanted : null;

            // As the screens count it: starting, or a relay on and not on its way down.
            var state = _board.State;
            var phase = runtime?.Phase ?? GroupPhases.Idle;
            var isOn = phase == GroupPhases.Starting
                || (phase != GroupPhases.Stopping && group.Members.Any(m => channels.RelayChannel(m.RelayId) is { } channel && state.IsOn(channel)));

            // On outside the app during its schedule's window: the schedule's, as for a relay.
            var turnedOnBySchedule = runtime?.TurnedOnBy == Actor;
            if (isOn && runtime?.TurnedOnBy == EventActors.External && groupWindows.Any(w => w.Contains(now)))
            {
                turnedOnBySchedule = await _groups.TakeOverOwnerlessAsync(group.Id, Actor, cancellationToken);
                changed |= turnedOnBySchedule;
            }

            var start = Decide(groupWindows, isOn, turnedOnBySchedule, retry, from, now, reconcile);
            if (start is not { } on)
            {
                continue;
            }

            try
            {
                if (on)
                {
                    await _groups.StartGroupAsync(group.Id, Actor, cancellationToken);
                }
                else
                {
                    await _groups.StopGroupAsync(group.Id, Actor, cancellationToken);
                }
            }
            catch (BoardNotConnectedException)
            {
                // Lost the board mid-way: the minute is handled again once it is back.
                Interlocked.Increment(ref _connectGeneration);
                return (changed, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One group that fails must not hold back the other groups, nor the relays' progress: holding back
                // the minute would start runs again that a person stopped. Logged, and this group alone is tried
                // again at the next tick.
                LogGroupFailed(ex, group.Id);
                _retryGroups[group.Id] = on;
                continue;
            }

            changed = true;
        }

        return (changed, false);
    }

    /// <summary>
    /// What a group's schedules want this tick: true to start it, false to stop it, null to leave it. By the relays'
    /// rules: a run starts it when it begins, at a reconcile or on a retry; it stops when no run covers it and the
    /// schedule started it, at a live OFF, or on a retried stop.
    /// </summary>
    /// <param name="windows">The group's runs that end after <paramref name="from"/>.</param>
    /// <param name="retry">What failed for this group at the last tick: a start (true), a stop (false), or nothing.</param>
    public static bool? Decide(
        IReadOnlyList<ScheduleWindow> windows,
        bool isOn,
        bool turnedOnBySchedule,
        bool? retry,
        DateTime from,
        DateTime now,
        bool reconcile)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var running = windows.Where(w => w.Contains(now)).OrderByDescending(w => w.OffUtc).FirstOrDefault();
        if (running is not null)
        {
            var startedNow = running.OnUtc > from && running.OnUtc <= now;
            return !isOn && (startedNow || reconcile || retry == true) ? true : null;
        }

        if (!isOn)
        {
            return null;
        }

        var ended = windows.Where(w => w.OffUtc > from && w.OffUtc <= now).OrderByDescending(w => w.OffUtc).FirstOrDefault();
        return turnedOnBySchedule || retry == false || (ended is not null && now - ended.OffUtc <= OnTimeTolerance) ? false : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await WaitForNextMinuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed tick must not stop the schedules for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTickFailed(ex);
                await WaitForNextMinuteAsync(stoppingToken);
            }
        }
    }

    // Wakes just after the next minute starts, or at once when the board comes back.
    private async Task WaitForNextMinuteAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var next = new DateTimeOffset(FloorToMinute(now.UtcDateTime), TimeSpan.Zero).AddMinutes(1).AddMilliseconds(500);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(next - now);
        try
        {
            await _wake.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The minute came.
        }
    }

    private void OnStateChanged(object? sender, BoardState state)
    {
        if (state.IsConnected && !_wasConnected)
        {
            Interlocked.Increment(ref _connectGeneration);
            if (_wake.CurrentCount == 0)
            {
                try
                {
                    _wake.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already woken.
                }
            }
        }

        _wasConnected = state.IsConnected;
    }

    private async Task<bool> SendAsync(int channel, bool on, CancellationToken cancellationToken)
    {
        var mask = _board.Capabilities.Relay(channel);
        _expected.Expect(mask, on);
        try
        {
            if (on)
            {
                await _board.TurnOnAsync(mask, cancellationToken);
            }
            else
            {
                await _board.TurnOffAsync(mask, cancellationToken);
            }

            return true;
        }
        catch (BoardNotConnectedException)
        {
            // Lost the board mid-way: the minute is handled again once it is back. Nothing is logged, so whatever the
            // board did shows as an external change.
            _expected.Forget(mask, on);
            Interlocked.Increment(ref _connectGeneration);
            return false;
        }
    }

    // Without a schedule id when the schedule that turned the relay on no longer runs it.
    private static void Log(ContactorDbContext db, int boardId, DateTime now, int relayId, string action, int? scheduleId)
    {
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = now,
            BoardId = boardId,
            RelayId = relayId,
            Actor = Actor,
            Action = action,
            Details = scheduleId is { } id ? JsonSerializer.Serialize(new { scheduleId = id }) : null,
        });
    }

    private static DateTime FloorToMinute(DateTime utc)
    {
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
    }

    private static DateTime Max(DateTime a, DateTime b)
    {
        return a > b ? a : b;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler could not start or stop group {GroupId}")]
    private partial void LogGroupFailed(Exception exception, int groupId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick failed")]
    private partial void LogTickFailed(Exception exception);
}
