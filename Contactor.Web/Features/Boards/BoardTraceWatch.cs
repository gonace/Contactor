namespace Contactor.Web.Features.Boards;

/// <summary>
/// Listens to every board's Debug trace (<see cref="LiveBoard.Trace"/>) for a page: the Debug screen and the tab bar's
/// toasts. A board added while it listens is listened to as well, and a board removed is let go. Disposing it lets go of every trace and of the
/// registry. The callbacks come on a board's connection loop, a timer or the registry's caller: they must be quick.
/// </summary>
public sealed class BoardTraceWatch : IDisposable
{
    private readonly BoardRegistry _registry;
    private readonly Action<LiveBoard, BoardTraceItem>? _added;
    private readonly Action? _changed;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Listener> _listeners = [];
    private bool _disposed;

    /// <param name="registry">The boards.</param>
    /// <param name="added">A new item in a board's trace, with its board.</param>
    /// <param name="changed">
    /// Anything a board's trace list shows changed: a new item, the routine count, a Clear, or a board added or removed. Null to
    /// hear only <paramref name="added"/>.
    /// </param>
    public BoardTraceWatch(BoardRegistry registry, Action<LiveBoard, BoardTraceItem>? added, Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _added = added;
        _changed = changed;
        _registry.BoardsChanged += OnBoardsChanged;
        ListenToNew();
    }

    public void Dispose()
    {
        _registry.BoardsChanged -= OnBoardsChanged;
        lock (_lock)
        {
            _disposed = true;
            foreach (var listener in _listeners.Values)
            {
                listener.Dispose();
            }

            _listeners.Clear();
        }
    }

    private void OnBoardsChanged(object? sender, EventArgs e)
    {
        ListenToNew();
        _changed?.Invoke();
    }

    private void ListenToNew()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var boards = _registry.All;
            foreach (var gone in _listeners.Keys.Where(id => !boards.Any(b => b.BoardId == id)).ToList())
            {
                _listeners[gone].Dispose();
                _listeners.Remove(gone);
            }

            foreach (var board in boards)
            {
                if (!_listeners.ContainsKey(board.BoardId))
                {
                    _listeners[board.BoardId] = new Listener(board, this);
                }
            }
        }
    }

    // One board's trace: its handlers, kept so the same ones are let go.
    private sealed class Listener : IDisposable
    {
        private readonly BoardTrace _trace;
        private readonly EventHandler<BoardTraceItem> _onAdded;
        private readonly EventHandler? _onChanged;

        public Listener(LiveBoard board, BoardTraceWatch watch)
        {
            _trace = board.Trace;
            _onAdded = (_, item) =>
            {
                watch._added?.Invoke(board, item);
                watch._changed?.Invoke();
            };
            _trace.Added += _onAdded;
            if (watch._changed is { } changed)
            {
                _onChanged = (_, _) => changed();
                _trace.RoutineCounted += _onChanged;
                _trace.Cleared += _onChanged;
            }
        }

        public void Dispose()
        {
            _trace.Added -= _onAdded;
            if (_onChanged is not null)
            {
                _trace.RoutineCounted -= _onChanged;
                _trace.Cleared -= _onChanged;
            }
        }
    }
}
