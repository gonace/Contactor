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
/// The schedules of one board's relays and groups (see <see cref="SchedulerService"/> for the rules): its own progress
/// (its <see cref="BoardRuntime.LastTickUtc"/>), its own connect generation and its own start or reconnect
/// reconciliation, and its own loop (every minute, on the minute, and at once when the board comes back), so a board
/// that is lost, slow or failing holds back only its own minutes, never another board's. Made for each live board,
/// subscribed before its connection starts.
/// </summary>
public sealed partial class BoardScheduler : IBoardPart, IAsyncDisposable
{
    private const string Actor = SchedulerService.Actor;

    // An OFF this late is a catch-up, not the minute's own work.
    private static readonly TimeSpan OnTimeTolerance = TimeSpan.FromMinutes(1);

    private readonly LiveBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly GroupSequencer _groups;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BoardScheduler> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _ended = new();

    // Every (re)connect bumps the generation; a tick reconciles when it hasn't handled the current one. A counter, not a
    // flag, so a reconnect during a tick isn't wiped out when that tick finishes.
    private int _connectGeneration = 1;
    private int _reconciledGeneration;
    private bool _wasConnected;
    private int _disposed;

    // Groups whose start (true) or stop (false) failed for another reason than a lost board: the next tick tries it
    // again, as it would for a relay. Only touched under the tick lock.
    private readonly Dictionary<int, bool> _retryGroups = [];

    internal BoardScheduler(
        LiveBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        GroupSequencer groups,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<BoardScheduler> logger)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _groups = groups;
        _expected = board.Expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;

        // From its making, before the board's connection starts: a reconnect must never go unnoticed.
        _board.StateChanged += OnStateChanged;
    }

    /// <summary>The board's row.</summary>
    public int BoardId => _board.BoardId;

    /// <summary>Stops listening and ends the loop; a tick under way finishes first, and none runs after.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _board.StateChanged -= OnStateChanged;
        await _tickGate.WaitAsync(CancellationToken.None);
        _tickGate.Release();
        await _ended.CancelAsync();
    }

    /// <summary>The board's loop: a tick every minute, on the minute, and at once when the board comes back.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ended.Token);
        var stoppingToken = linked.Token;
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
#pragma warning disable CA1031 // One failed tick must not stop the board's schedules for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTickFailed(ex, BoardId);
                try
                {
                    await WaitForNextMinuteAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    // Wakes just after the next minute starts, or at once when the board comes back.
    private async Task WaitForNextMinuteAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var next = new DateTimeOffset(FloorToMinute(now.UtcDateTime), TimeSpan.Zero).AddMinutes(1).AddMilliseconds(500);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(next - now, _timeProvider, timeout.Token);
        var woken = _wake.WaitAsync(timeout.Token);
        await Task.WhenAny(delay, woken);
        await timeout.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await Task.WhenAll(delay, woken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The other one came first.
        }
    }

    /// <summary>
    /// Handles everything due on this board up to the current minute. Does nothing while the board is disconnected, so
    /// the minutes it misses are caught up once it is back.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken);
        try
        {
            var state = _board.State;
            if (!state.IsConnected || Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            var generation = Volatile.Read(ref _connectGeneration);
            var reconcile = generation != _reconciledGeneration;
            var now = FloorToMinute(_clock.UtcNow);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            // The board's runtime row holds its progress; without the board's row there is nothing to run.
            if (await db.BoardRuntimeAsync(BoardId, cancellationToken) is not { } progress)
            {
                return;
            }

            // Handles (from, now]; the very first run starts from now, with nothing to catch up. Only this board's
            // relays' and groups' schedules.
            var from = progress.LastTickUtc is { } last ? Max(last, now - SchedulerService.MaxCatchUp) : now;
            var channels = await BoardChannels.LoadAsync(db, BoardId, cancellationToken);
            var relayIds = channels.AllRelayIds.ToList();
            var groupIds = db.RelayGroups.Where(g => g.BoardId == BoardId).Select(g => g.Id);
            var schedules = await db.Schedules.AsNoTracking()
                .Where(s => s.IsEnabled && ((s.RelayId != null && relayIds.Contains(s.RelayId.Value)) || (s.GroupId != null && groupIds.Contains(s.GroupId.Value))))
                .ToListAsync(cancellationToken);
            var windows = ScheduleCalendar.Windows(schedules, _clock.Zone, from, now);
            var runtimes = await db.RelayRuntimes.Where(r => relayIds.Contains(r.RelayId)).ToDictionaryAsync(r => r.RelayId, cancellationToken);
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
        // Read outside the sequencer's lock, so it can be a moment old; Start and Stop look again under the lock. Only
        // this board's groups.
        var groups = await db.RelayGroups.AsNoTracking().Include(g => g.Members).Where(g => g.BoardId == BoardId).ToListAsync(cancellationToken);
        var groupIds = groups.Select(g => g.Id).ToList();
        var runtimes = await db.GroupRuntimes.AsNoTracking().Where(r => groupIds.Contains(r.GroupId)).ToDictionaryAsync(r => r.GroupId, cancellationToken);
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

            var start = SchedulerService.Decide(groupWindows, isOn, turnedOnBySchedule, retry, from, now, reconcile);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick on relay board {BoardId} failed")]
    private partial void LogTickFailed(Exception exception, int boardId);
}
