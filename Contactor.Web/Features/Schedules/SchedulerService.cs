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
/// (<see cref="IBoardWatcher"/>): every minute each board's part ticks on its own, and a board that is lost holds back
/// only its own catch-up. A board that comes back wakes the scheduler at once.
/// </para>
/// </summary>
public sealed partial class SchedulerService : BackgroundService, IBoardWatcher
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
    private readonly ILogger<SchedulerService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _lock = new();

    // Replaced as a whole, so a reader never sees it change.
    private IReadOnlyList<BoardScheduler> _boards = [];

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
        _logger = loggerFactory.CreateLogger<SchedulerService>();
    }

    /// <summary>The board's part, or null for a board the app doesn't run.</summary>
    public BoardScheduler? For(int boardId)
    {
        return Volatile.Read(ref _boards).FirstOrDefault(b => b.BoardId == boardId);
    }

    /// <inheritdoc/>
    public IAsyncDisposable Watch(LiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var scheduler = new BoardScheduler(board, _dbFactory, _clock, _groups, _changes, _loggerFactory.CreateLogger<BoardScheduler>(), Wake);
        lock (_lock)
        {
            Volatile.Write(ref _boards, [.. _boards, scheduler]);
        }

        return new Watching(this, scheduler);
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Handles everything due up to the current minute on every board, each on its own: a board that is disconnected
    /// does nothing, so the minutes it misses are caught up once it is back, and one that fails or is slow doesn't keep
    /// the others from their minute. Throws the first failure once all have ticked.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(Volatile.Read(ref _boards).Select(b => b.TickAsync(cancellationToken)));
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
        var next = new DateTimeOffset(BoardScheduler.FloorToMinute(now.UtcDateTime), TimeSpan.Zero).AddMinutes(1).AddMilliseconds(500);
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

    // A board that comes back: the next tick at once.
    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
        catch (ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private void Remove(BoardScheduler scheduler)
    {
        lock (_lock)
        {
            Volatile.Write(ref _boards, [.. _boards.Where(b => !ReferenceEquals(b, scheduler))]);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick failed")]
    private partial void LogTickFailed(Exception exception);

    // Ends one board's watch: its part leaves the list, then stops.
    private sealed class Watching : IAsyncDisposable
    {
        private readonly SchedulerService _service;
        private readonly BoardScheduler _scheduler;

        public Watching(SchedulerService service, BoardScheduler scheduler)
        {
            _service = service;
            _scheduler = scheduler;
        }

        public ValueTask DisposeAsync()
        {
            _service.Remove(_scheduler);
            return _scheduler.DisposeAsync();
        }
    }
}
