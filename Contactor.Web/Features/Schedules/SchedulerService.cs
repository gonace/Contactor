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
/// <para>
/// Each board has its own part (<see cref="BoardScheduler"/>), with its own progress, made with its live board
/// (<see cref="IBoardWatcher"/>) and its own loop: every minute each board ticks on its own, a board that comes back
/// wakes only its own loop at once, and a board that is lost, slow or failing holds back only its own minutes.
/// </para>
/// </summary>
public sealed class SchedulerService : BackgroundService, IBoardWatcher
{
    public const string Actor = EventActors.Schedule;

    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    // An OFF this late is a catch-up, not the minute's own work.
    private static readonly TimeSpan OnTimeTolerance = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly GroupSequencer _groups;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BoardParts<BoardScheduler> _boards;

    public SchedulerService(
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        GroupSequencer groups,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _dbFactory = dbFactory;
        _clock = clock;
        _groups = groups;
        _changes = changes;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _boards = new BoardParts<BoardScheduler>(loggerFactory.CreateLogger<SchedulerService>());
    }

    /// <summary>How many boards' loops run now (see <see cref="BoardParts{T}.RunningLoops"/>).</summary>
    public int RunningLoops => _boards.RunningLoops;

    /// <summary>The board's part, or null for a board the app doesn't run.</summary>
    public BoardScheduler? For(int boardId)
    {
        return _boards.Find(boardId);
    }

    /// <inheritdoc/>
    public IAsyncDisposable Watch(LiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var scheduler = new BoardScheduler(board, _dbFactory, _clock, _groups, _changes, _timeProvider, _loggerFactory.CreateLogger<BoardScheduler>());
        _boards.Add(scheduler);
        return new Watching(_boards, scheduler);
    }

    /// <summary>
    /// Handles everything due up to the current minute on every board at once, each on its own (as each board's loop
    /// does every minute): a board that is disconnected does nothing, so the minutes it misses are caught up once it is
    /// back. Throws the first failure once all have ticked.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_boards.All.Select(b => b.TickAsync(cancellationToken)));
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _boards.RunAsync(stoppingToken);
    }

    // Ends one board's watch: its part leaves the list, then stops.
    private sealed class Watching : IAsyncDisposable
    {
        private readonly BoardParts<BoardScheduler> _boards;
        private readonly BoardScheduler _scheduler;

        public Watching(BoardParts<BoardScheduler> boards, BoardScheduler scheduler)
        {
            _boards = boards;
            _scheduler = scheduler;
        }

        public ValueTask DisposeAsync()
        {
            _boards.Remove(_scheduler);
            return _scheduler.DisposeAsync();
        }
    }
}
