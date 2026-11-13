using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>A step on the board connection as the Debug screen and the toasts show it.</summary>
/// <param name="Id">Increases by one per item, so a page can tell new ones apart.</param>
/// <param name="Entry">The step, as the board described it.</param>
public sealed record BoardTraceItem(long Id, BoardTraceEntry Entry)
{
    public DateTimeOffset AtUtc => Entry.At;

    public BoardTraceDirection Direction => Entry.Direction;

    public string Text => Entry.Text;

    /// <summary>The message's bytes as text, or null when there is no message.</summary>
    public string? Hex => Entry.Bytes;

    public bool IsError => Entry.IsError;

    public bool IsRoutine => Entry.IsRoutine;
}

/// <summary>
/// The latest steps on the board connection, in memory only (nothing is stored, and a restart empties it), for the
/// Debug screen. It listens from the moment it is created, which <see cref="BoardHostedService"/> makes happen before
/// the first connection. Routine steps (a keep-alive, a poll that changes nothing) are only counted, so they never
/// push the steps worth reading out of the list, however often a board sends them.
/// </summary>
public sealed partial class BoardTrace : IDisposable
{
    public const int Capacity = 100;

    /// <summary>How often, at most, <see cref="RoutineCounted"/> tells the screens the count moved.</summary>
    public static readonly TimeSpan RoutineNoticeInterval = TimeSpan.FromSeconds(1);

    private readonly IBoardTraffic _traffic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BoardTrace>? _logger;
    private readonly Lock _lock = new();
    private readonly Queue<BoardTraceItem> _items = new(Capacity);
    private readonly ITimer _heldNoticeTimer;
    private long _nextId = 1;
    private long _routineLeftOut;
    private DateTimeOffset? _routineNoticedAt;
    private bool _noticeHeld;

    public BoardTrace(IBoardTraffic traffic, TimeProvider timeProvider, ILogger<BoardTrace>? logger = null)
    {
        _logger = logger;
        _traffic = traffic;
        _timeProvider = timeProvider;
        _heldNoticeTimer = _timeProvider.CreateTimer(_ => RaiseHeldNotice(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _traffic.Traced += OnTraced;
    }

    /// <summary>
    /// Raised with each new item, on the board connection's loop: handlers must be quick. Not raised for a routine step,
    /// which is only counted.
    /// </summary>
    public event EventHandler<BoardTraceItem>? Added;

    /// <summary>
    /// Raised when <see cref="RoutineLeftOut"/> grew, at most once every <see cref="RoutineNoticeInterval"/>, so an open
    /// Debug screen keeps its count current without re-rendering for every poll. A step that comes sooner is told at the
    /// end of that interval, so the count shown never stays behind. On the board connection's loop, or on a timer.
    /// </summary>
    public event EventHandler? RoutineCounted;

    /// <summary>Raised when <see cref="Clear"/> empties the list.</summary>
    public event EventHandler? Cleared;

    /// <summary>The items, newest first.</summary>
    public IReadOnlyList<BoardTraceItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items.Reverse()];
            }
        }
    }

    /// <summary>How many routine steps were counted instead of listed since the start or the last <see cref="Clear"/>.</summary>
    public long RoutineLeftOut
    {
        get
        {
            lock (_lock)
            {
                return _routineLeftOut;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _routineLeftOut = 0;
            _routineNoticedAt = null;
            StopHeldNotice();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _traffic.Traced -= OnTraced;
        _heldNoticeTimer.Dispose();
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        Record(entry);
    }

    /// <summary>Adds a step, or counts a routine one; the board's <see cref="IBoardTraffic.Traced"/> calls it, and the tests do.</summary>
    public void Record(BoardTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsRoutine)
        {
            CountRoutine();
            return;
        }

        BoardTraceItem item;
        lock (_lock)
        {
            item = new BoardTraceItem(_nextId++, entry);
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }

        Added?.Invoke(this, item);
    }

    private void CountRoutine()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            _routineLeftOut++;
            if (_routineNoticedAt is { } last && now - last < RoutineNoticeInterval)
            {
                // Inside the interval: one timer tells the screens at its end, however many steps come before.
                if (!_noticeHeld)
                {
                    _noticeHeld = true;
                    _heldNoticeTimer.Change(last + RoutineNoticeInterval - now, Timeout.InfiniteTimeSpan);
                }

                return;
            }

            _routineNoticedAt = now;
            StopHeldNotice();
        }

        RaiseRoutineCounted();
    }

    private void RaiseHeldNotice()
    {
        lock (_lock)
        {
            if (!_noticeHeld)
            {
                return;
            }

            _noticeHeld = false;
            _routineNoticedAt = _timeProvider.GetUtcNow();
        }

        RaiseRoutineCounted();
    }

    // Each subscriber on its own, as the boards do: this may run on a timer's thread, where an exception would end the
    // app, and one subscriber that fails must not keep the count from the others.
    private void RaiseRoutineCounted()
    {
        if (RoutineCounted is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A subscriber's failure is logged; the others are still told.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is not null)
                {
                    LogListenerFailed(_logger, ex);
                }
            }
        }
    }

    private void StopHeldNotice()
    {
        if (_noticeHeld)
        {
            _noticeHeld = false;
            _heldNoticeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the board traffic's routine count failed")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception);
}
