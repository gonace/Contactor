namespace Contactor.Web.Features.Boards;

/// <summary>
/// What the app runs for each board beside its connection: the safety services' part for that board (its groups, its
/// event recorder, its schedules). <see cref="BoardRegistry"/> asks every watcher, in the order they are registered,
/// to watch a live board as soon as it makes it, before its connection ever starts, so no status goes unseen; the
/// watch ends (unsubscribes, stops its loop) when the live board is disposed.
/// </summary>
public interface IBoardWatcher
{
    /// <summary>
    /// Starts watching <paramref name="board"/>: subscribes to its state at once. Returns what ends the watch; the live
    /// board owns it and disposes it before itself.
    /// </summary>
    IAsyncDisposable Watch(LiveBoard board);
}

/// <summary>One board's part of a service, with a loop the service runs for it (see <see cref="BoardParts{T}"/>).</summary>
public interface IBoardPart
{
    /// <summary>The board's row (<see cref="Persistence.Board.Id"/>).</summary>
    int BoardId { get; }

    /// <summary>The part's own loop, until cancelled or the part is disposed. Must not throw but for the cancellation.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Every board's part of one service: found by the board (<see cref="Find"/>), and each part's loop run while the
/// service runs (<see cref="RunAsync"/>), also for a part added meanwhile (a board made while the app runs). A part
/// removed (its board's watch ended) stops its own loop.
/// </summary>
public sealed class BoardParts<T>
    where T : class, IBoardPart
{
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    // Replaced as a whole, so a reader never sees it change.
    private IReadOnlyList<T> _parts = [];

    // While RunAsync runs: its token, and each part's loop.
    private CancellationToken? _running;
    private readonly List<Task> _loops = [];

    public BoardParts(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Every board's part, in the order the boards were made.</summary>
    public IReadOnlyList<T> All => Volatile.Read(ref _parts);

    /// <summary>The board's part, or null when the board has none (no such board, or its watch ended).</summary>
    public T? Find(int boardId)
    {
        return All.FirstOrDefault(p => p.BoardId == boardId);
    }

    /// <summary>Adds a board's part; its loop starts at once while the service runs.</summary>
    public void Add(T part)
    {
        ArgumentNullException.ThrowIfNull(part);
        lock (_lock)
        {
            if (_parts.Any(p => p.BoardId == part.BoardId))
            {
                throw new InvalidOperationException($"Relay board {part.BoardId} already has its part.");
            }

            Volatile.Write(ref _parts, [.. _parts, part]);
            if (_running is { } token)
            {
                Start(part, token);
            }
        }
    }

    /// <summary>Removes a board's part. Its loop ends with the part itself (its disposal).</summary>
    public void Remove(T part)
    {
        lock (_lock)
        {
            Volatile.Write(ref _parts, [.. _parts.Where(p => !ReferenceEquals(p, part))]);
        }
    }

    /// <summary>Runs every part's loop, and a part's added meanwhile, until cancelled; then waits for them to end.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException("The boards' parts already run.");
            }

            _running = cancellationToken;
            foreach (var part in _parts)
            {
                Start(part, cancellationToken);
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }

        Task[] loops;
        lock (_lock)
        {
            _running = null;
            loops = [.. _loops];
            _loops.Clear();
        }

        // Each loop ends on the cancellation; one that failed was logged.
        await Task.WhenAll(loops.Select(l => l.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
    }

    // Only under the lock.
    private void Start(T part, CancellationToken token)
    {
        var loop = Task.Run(() => part.RunAsync(token), CancellationToken.None);
        _loops.Add(loop);
        _ = loop.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _logger.BoardPartFailed(t.Exception!.GetBaseException(), typeof(T).Name, part.BoardId);
                }

                lock (_lock)
                {
                    _loops.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal static partial class BoardPartsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Part} of relay board {BoardId} stopped")]
    public static partial void BoardPartFailed(this ILogger logger, Exception exception, string part, int boardId);
}
