using Contactor.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>A board's new state, as <see cref="BoardRegistry.StateChanged"/> tells it.</summary>
/// <param name="BoardId">The board's row (<see cref="Board.Id"/>).</param>
/// <param name="State">Its state now.</param>
public sealed record BoardStateChange(int BoardId, BoardState State);

/// <summary>
/// Every relay board the app runs, one <see cref="LiveBoard"/> per board's row, for the app's lifetime: made at start
/// for every row (<see cref="LoadAsync"/>), and given a model while the app runs (<see cref="ReplaceModelAsync"/>, the
/// setup guide's choice). <see cref="BoardHostedService"/> keeps every board's connection open
/// (<see cref="RunAsync"/>). Only the first board takes the address and login in its model's settings section
/// (install.sh's <c>Vm201__Host</c> and so on); every board takes the ones saved on its own row.
/// </summary>
public sealed partial class BoardRegistry : IAsyncDisposable
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly IServiceProvider _app;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BoardRegistry> _logger;
    private readonly Lock _lock = new();

    // One change to the boards at a time: loading, a new model.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // In the list's order (SortOrder, then id); replaced as a whole, so a reader never sees it change.
    private IReadOnlyList<LiveBoard> _boards = [];

    // While RunAsync runs: its token, and each board's run.
    private CancellationToken? _runToken;
    private readonly List<Task> _runs = [];
    private TaskCompletionSource _runFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public BoardRegistry(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IServiceProvider app,
        IConfiguration configuration,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _dbFactory = dbFactory;
        _app = app;
        _configuration = configuration;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BoardRegistry>();
        Only = new OnlyBoard(this, time, loggerFactory.CreateLogger<OnlyBoard>());
    }

    /// <summary>
    /// A board's new state, raised on that board's connection loop (handlers must be quick), and with a board's state,
    /// unknown until it answers, when it gets a new model.
    /// </summary>
    public event EventHandler<BoardStateChange>? StateChanged;

    /// <summary>
    /// Raised when a board is added to <see cref="All"/>, while the boards are being changed: handlers must not change
    /// them in turn.
    /// </summary>
    public event EventHandler? BoardsChanged;

    /// <summary>Every board, in the list's order.</summary>
    public IReadOnlyList<LiveBoard> All => Volatile.Read(ref _boards);

    /// <summary>
    /// The one board, for what still runs one board only: see <see cref="OnlyBoard"/>.
    /// Until PR 4 (Issue #4): one board.
    /// </summary>
    public OnlyBoard Only { get; }

    /// <summary>The board on this row, or null when the app runs none there.</summary>
    public LiveBoard? Find(int boardId)
    {
        return All.FirstOrDefault(b => b.BoardId == boardId);
    }

    /// <summary>
    /// At start: a live board for every board's row there isn't one for yet, each with its saved model
    /// (<see cref="Board.Type"/>), if any. Throws for a saved model the catalogue doesn't have.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<Board> rows;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                rows = await db.Boards.AsNoTracking().OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(cancellationToken);
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (Find(row.Id) is not null)
                {
                    continue;
                }

                BoardRegistration? registration = null;
                if (row.Type.Length > 0)
                {
                    registration = BoardCatalogue.Find(row.Type)
                        ?? throw new InvalidOperationException(
                            $"Relay board \"{row.Name}\" is a \"{row.Type}\", which isn't a board this version of the app knows. Use one of: {BoardCatalogue.Ids}.");
                }

                var live = Add(row);
                if (registration is not null)
                {
                    await live.ReplaceAsync(Build(registration, BoardSetup.From(row, isFirst: i == 0)));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="model"/> on the board's row from now on, unless it already is its model: the setup guide's
    /// choice. A row made since start (the guide's first board) gets its live board now. Built before anything changes,
    /// so a model the app can't build (e.g. its settings are wrong) leaves the board as it was. Doesn't save the model.
    /// </summary>
    public async Task<LiveBoard> ReplaceModelAsync(int boardId, BoardModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var registration = BoardCatalogue.Find(model.Id)
            ?? throw new ArgumentException($"\"{model.Id}\" isn't a board in the catalogue.", nameof(model));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (row, isFirst) = await RowAsync(boardId, cancellationToken);
            var live = Find(boardId);
            if (live?.Model?.Id == registration.Model.Id)
            {
                return live;
            }

            var built = Build(registration, BoardSetup.From(row, isFirst));
            try
            {
                live ??= Add(row);
            }
            catch
            {
                await built.DisposeAsync();
                throw;
            }

            live.Name = row.Name;

            // From here on the live board owns it, also when it refuses it.
            await live.ReplaceAsync(built);
            return live;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="board"/>, a model already built for the board's row, on that row from now on. A row made
    /// since start gets its live board now. For a board built other than from the catalogue, e.g. in a test.
    /// </summary>
    public async Task<LiveBoard> ReplaceAsync(ActiveBoard board, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (row, _) = await RowAsync(board.BoardId, cancellationToken);
            var live = Find(board.BoardId) ?? Add(row);
            await live.ReplaceAsync(board);
            return live;
        }
        catch
        {
            await board.DisposeAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs every board until cancelled, each on its own (<see cref="LiveBoard.RunAsync"/>), and a board added while it
    /// runs from then on. A board whose run fails ends the others' and fails this, as one board's did before.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task failed;
        lock (_lock)
        {
            if (_runToken is not null)
            {
                throw new InvalidOperationException("The boards already run.");
            }

            _runToken = stopping.Token;
            failed = _runFailed.Task;
            foreach (var board in All)
            {
                StartRun(board);
            }
        }

        Exception? failure = null;
        try
        {
            await failed.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping.
        }
#pragma warning disable CA1031 // A board's run failed: the others stop first, then it is thrown again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        Task[] runs;
        lock (_lock)
        {
            _runToken = null;
            runs = [.. _runs];
            _runs.Clear();
            _runFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await stopping.CancelAsync();
        try
        {
            await Task.WhenAll(runs);
        }
        catch when (failure is not null)
        {
            // The first failure is the one thrown.
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IReadOnlyList<LiveBoard> boards;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            boards = _boards;
            Volatile.Write(ref _boards, []);
        }

        foreach (var board in boards)
        {
            board.StateChanged -= OnStateChanged;
            await board.DisposeAsync();
        }

        Only.Dispose();
        _gate.Dispose();
    }

    private async Task<(Board Row, bool IsFirst)> RowAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken)
            ?? throw new InvalidOperationException($"There is no relay board {boardId}.");
        return (row, await db.FirstBoardIdAsync(cancellationToken) == boardId);
    }

    private ActiveBoard Build(BoardRegistration registration, BoardSetup setup)
    {
        return registration.Create(registration.Model, setup, _app, _configuration);
    }

    // Only under the gate. Subscribed before it gets a model, so its first state reaches the listeners.
    private LiveBoard Add(Board row)
    {
        var live = new LiveBoard(row.Id, row.Name, _dbFactory, _time, _loggerFactory);
        live.StateChanged += OnStateChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                live.StateChanged -= OnStateChanged;
                _ = live.DisposeAsync().AsTask();
                throw new ObjectDisposedException(nameof(BoardRegistry));
            }

            // In the list's order: a new row is last, as it is added with the highest sort order.
            Volatile.Write(ref _boards, [.. _boards, live]);
            if (_runToken is not null)
            {
                StartRun(live);
            }
        }

        LogAdded(row.Id);
        RaiseBoardsChanged();
        return live;
    }

    // Only under the lock, while RunAsync runs.
    private void StartRun(LiveBoard board)
    {
        var token = _runToken!.Value;
        var failed = _runFailed;
        var run = Task.Run(() => board.RunAsync(token), CancellationToken.None);
        _runs.Add(run);
        _ = run.ContinueWith(
            t => failed.TrySetException(t.Exception!.InnerExceptions),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnStateChanged(object? sender, BoardState state)
    {
        if (sender is not LiveBoard board || StateChanged is not { } handlers)
        {
            return;
        }

        var change = new BoardStateChange(board.BoardId, state);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardStateChange>>())
        {
            try
            {
                handler(this, change);
            }
#pragma warning disable CA1031 // A subscriber's failure is logged; the others still get the state.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(ex, board.BoardId);
            }
        }
    }

    private void RaiseBoardsChanged()
    {
        if (BoardsChanged is not { } handlers)
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
                LogBoardsListenerFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Relay board {BoardId} added")]
    private partial void LogAdded(int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to relay board {BoardId}'s state failed")]
    private partial void LogSubscriberFailed(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the list of relay boards failed")]
    private partial void LogBoardsListenerFailed(Exception exception);
}
