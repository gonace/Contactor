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
public sealed class BoardTrace : IDisposable
{
    public const int Capacity = 100;

    private readonly IBoardTraffic _traffic;
    private readonly Lock _lock = new();
    private readonly Queue<BoardTraceItem> _items = new(Capacity);
    private long _nextId = 1;
    private long _routineLeftOut;

    public BoardTrace(IBoardTraffic traffic)
    {
        _traffic = traffic;
        _traffic.Traced += OnTraced;
    }

    /// <summary>
    /// Raised with each new item, on the board connection's loop: handlers must be quick. Not raised for a routine step,
    /// which is only counted.
    /// </summary>
    public event EventHandler<BoardTraceItem>? Added;

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
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _traffic.Traced -= OnTraced;
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        Record(entry);
    }

    /// <summary>Adds a step, or counts a routine one; the board's <see cref="IBoardTraffic.Traced"/> calls it, and the tests do.</summary>
    public void Record(BoardTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        BoardTraceItem item;
        lock (_lock)
        {
            if (entry.IsRoutine)
            {
                _routineLeftOut++;
                return;
            }

            item = new BoardTraceItem(_nextId++, entry);
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }

        Added?.Invoke(this, item);
    }
}
