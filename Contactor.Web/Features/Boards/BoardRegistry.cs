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
/// for every row (<see cref="LoadAsync"/>), given a model while the app runs (<see cref="ReplaceModelAsync"/>, the
/// setup guide's choice), and added while it runs (<see cref="AddBoardAsync(BoardModel, string?, CancellationToken)"/>,
/// "Add a board"). <see cref="BoardHostedService"/> keeps every board's connection open
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
    private readonly IReadOnlyList<IBoardWatcher> _watchers;
    private readonly Lock _lock = new();

    // One change to the boards at a time: loading, a new model, adding, removing and restoring a board.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // In the list's order (SortOrder, then id); replaced as a whole, so a reader never sees it change.
    private IReadOnlyList<LiveBoard> _boards = [];

    // While RunAsync runs: its token, and each board's run.
    private CancellationToken? _runToken;
    private readonly List<Task> _runs = [];
    private TaskCompletionSource _runFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    // Boards taken out by RemoveAsync whose rows may still be there: LoadAsync doesn't make them again. Under the gate.
    private readonly HashSet<int> _removed = [];

    public BoardRegistry(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IServiceProvider app,
        IConfiguration configuration,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        IEnumerable<IBoardWatcher>? watchers = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _watchers = [.. watchers ?? []];
        _dbFactory = dbFactory;
        _app = app;
        _configuration = configuration;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BoardRegistry>();
    }

    /// <summary>
    /// A board's new state, raised on that board's connection loop (handlers must be quick), and with a board's state,
    /// unknown until it answers, when it gets a new model.
    /// </summary>
    public event EventHandler<BoardStateChange>? StateChanged;

    /// <summary>
    /// Raised when a board is added to <see cref="All"/> or removed from it, while the boards are being changed:
    /// handlers must not change them in turn.
    /// </summary>
    public event EventHandler? BoardsChanged;

    /// <summary>Every board, in the list's order.</summary>
    public IReadOnlyList<LiveBoard> All => Volatile.Read(ref _boards);

    /// <summary>The board on this row, or null when the app runs none there.</summary>
    public LiveBoard? Find(int boardId)
    {
        return All.FirstOrDefault(b => b.BoardId == boardId);
    }

    /// <summary>
    /// Whether the relay on this board and channel is one the board has: its board runs and has that many relays. A
    /// row above the count, left from a board that had more, isn't shown, offered or switched.
    /// </summary>
    public bool HasRelay(int boardId, int channel)
    {
        return channel >= 1 && Find(boardId) is { } board && channel <= board.Capabilities.RelayCount;
    }

    /// <summary>Whether the input on this board and channel is one the board has, as <see cref="HasRelay"/>.</summary>
    public bool HasInput(int boardId, int channel)
    {
        return channel >= 1 && Find(boardId) is { } board && channel <= board.Capabilities.InputCount;
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
                if (Find(row.Id) is not null || _removed.Contains(row.Id))
                {
                    continue;
                }

                await LoadRowAsync(row, isFirst: i == 0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the board on this row run again after <see cref="RemoveAsync"/>, when removing it didn't go ahead (its rows
    /// are still there): in its place in the list, with its saved model. Does nothing when it runs or has no row.
    /// </summary>
    public async Task RestoreAsync(int boardId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _removed.Remove(boardId);
            if (Find(boardId) is not null)
            {
                return;
            }

            Board? row;
            bool isFirst;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                row = await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken);
                isFirst = await db.FirstBoardIdAsync(cancellationToken) == boardId;
            }

            if (row is not null)
            {
                await LoadRowAsync(row, isFirst);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets that the board on this row was removed, once its rows are gone: a new board may get its id again.
    /// </summary>
    public async Task ForgetRemovedAsync(int boardId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _removed.Remove(boardId);
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
                live ??= await AddAsync(row);
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
            var live = Find(board.BoardId) ?? await AddAsync(row);
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
    /// "Add a board": a new board of <paramref name="model"/>, last in the list, running from now on. Its row is made with
    /// "no address and no login" saved (the model's default port), so it never takes the address and login in the
    /// model's settings section, which are install.sh's for the first board it set up, also when it is the only board
    /// now: its address is typed in the steps that follow. Then its relays and inputs get their rows by channel, and its
    /// live board is watched by every safety service before its connection starts, as at start. An empty
    /// <paramref name="name"/> gives it the model's name, made unique ("Velleman VM201 2"). If it can't be made all the
    /// way, nothing of it is left. Throws <see cref="ArgumentException"/> for a name longer than
    /// <see cref="Board.NameMaxLength"/>.
    /// </summary>
    public Task<LiveBoard> AddBoardAsync(BoardModel model, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var registration = BoardCatalogue.Find(model.Id)
            ?? throw new ArgumentException($"\"{model.Id}\" isn't a board in the catalogue.", nameof(model));
        return AddBoardAsync(registration.Model, name, setup => Build(registration, setup), cancellationToken);
    }

    /// <summary>
    /// As <see cref="AddBoardAsync(BoardModel, string?, CancellationToken)"/>, with the board built by
    /// <paramref name="build"/>, e.g. in a test.
    /// </summary>
    public async Task<LiveBoard> AddBoardAsync(BoardModel model, string? name, Func<BoardSetup, ActiveBoard> build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(build);
        name = name?.Trim() ?? "";
        if (name.Length > Board.NameMaxLength)
        {
            throw new ArgumentException($"The name can be at most {Board.NameMaxLength} characters.", nameof(name));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Board row;
            bool isFirst;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var names = await db.Boards.Select(b => b.Name).ToListAsync(cancellationToken);
                isFirst = names.Count == 0;
                row = new Board
                {
                    Name = name.Length > 0 ? name : UniqueName(model, names),
                    Type = model.Id,
                    Host = "",
                    Port = model.DefaultPort,
                    Username = "",
                    Password = "",
                    SortOrder = (await db.Boards.MaxAsync(b => (int?)b.SortOrder, cancellationToken) ?? 0) + 1,
                };
                db.Boards.Add(row);
                await db.SaveChangesAsync(cancellationToken);
            }

            // A safeguard: the ids are never used again (AUTOINCREMENT), but a new row is a board of its own whatever
            // was removed before.
            _removed.Remove(row.Id);
            LiveBoard? live = null;
            try
            {
                await _app.SeedBoardAsync(row.Id, model.Capabilities.RelayCount, model.Capabilities.InputCount, cancellationToken);
                var built = build(BoardSetup.From(row, isFirst));
                try
                {
                    live = await AddAsync(row);
                }
                catch
                {
                    await built.DisposeAsync();
                    throw;
                }

                // From here on the live board owns it, also when it refuses it.
                await live.ReplaceAsync(built);
            }
            catch
            {
                // Nothing of it is left: its live board first (its watches end, then its connection), then its rows.
                if (live is not null)
                {
                    await TakeOutAsync(live);
                }

                await DeleteRowsAsync(row.Id);
                throw;
            }

            // Told once more now that it has its model: a screen that reloaded when it was added saw it without relays.
            RaiseBoardsChanged();
            LogBoardAdded(row.Id, model.Id);
            return live;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The name a new board of <paramref name="model"/> gets: the model's ("Velleman VM201"), or with a number after it
    /// when a board already has that name ("Velleman VM201 2"), within <see cref="Board.NameMaxLength"/>.
    /// </summary>
    public static string UniqueName(BoardModel model, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(taken);
        var name = Board.DefaultName(model.Name);
        for (var n = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
        {
            var suffix = $" {n}";
            var stem = Board.DefaultName(model.Name);
            name = (stem.Length + suffix.Length <= Board.NameMaxLength ? stem : stem[..(Board.NameMaxLength - suffix.Length)].TrimEnd()) + suffix;
        }

        return name;
    }

    /// <summary>
    /// Takes the board on this row out of the app, before its rows are deleted (<c>BoardRemoval</c>): out of
    /// <see cref="All"/> first, so nothing new is sent to it, then disposed (a step under way finishes, its watches end,
    /// then its connection), and <see cref="BoardsChanged"/> is raised. Its run ends quietly. Returns false when the app
    /// runs no board there. Doesn't touch the database; <see cref="LoadAsync"/> leaves it out until
    /// <see cref="RestoreAsync"/> makes it again (removing it didn't go ahead) or <see cref="ForgetRemovedAsync"/> (its rows are gone).
    /// </summary>
    public async Task<bool> RemoveAsync(int boardId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LiveBoard? live;
            lock (_lock)
            {
                live = _boards.FirstOrDefault(b => b.BoardId == boardId);
                if (live is null || _disposed)
                {
                    return false;
                }

                _removed.Add(boardId);
            }

            await TakeOutAsync(live);
            LogRemoved(boardId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs every board until cancelled, each on its own (<see cref="LiveBoard.RunAsync"/>), and a board added while it
    /// runs from then on. A board whose run fails, or ends cancelled while nothing stops it, ends the others' and fails
    /// this, as one board's did before.
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

        _gate.Dispose();
    }

    // Only under the gate: out of All first, so nothing new is sent to it, then disposed (a step under way finishes, its
    // watches end, then its connection); its run ends quietly.
    private async Task TakeOutAsync(LiveBoard live)
    {
        lock (_lock)
        {
            Volatile.Write(ref _boards, [.. _boards.Where(b => !ReferenceEquals(b, live))]);
        }

        live.StateChanged -= OnStateChanged;
        await live.DisposeAsync();
        RaiseBoardsChanged();
    }

    // A board that couldn't be added all the way: its rows go again. A failure here is logged; the first one is thrown.
    private async Task DeleteRowsAsync(int boardId)
    {
        try
        {
            await BoardRemoval.DeleteAsync(_dbFactory, boardId, CancellationToken.None);
        }
#pragma warning disable CA1031 // The failure to add is the one thrown; this one is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotDeleted(ex, boardId);
        }
    }

    private async Task<(Board Row, bool IsFirst)> RowAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken)
            ?? throw new InvalidOperationException($"There is no relay board {boardId}.");
        return (row, await db.FirstBoardIdAsync(cancellationToken) == boardId);
    }

    // Only under the gate: the row's live board, with its saved model, if any. Throws for a saved model the catalogue
    // doesn't have.
    private async Task LoadRowAsync(Board row, bool isFirst)
    {
        BoardRegistration? registration = null;
        if (row.Type.Length > 0)
        {
            registration = BoardCatalogue.Find(row.Type)
                ?? throw new InvalidOperationException(
                    $"Relay board \"{row.Name}\" is a \"{row.Type}\", which isn't a board this version of the app knows. Use one of: {BoardCatalogue.Ids}.");
        }

        var live = await AddAsync(row);
        if (registration is not null)
        {
            await live.ReplaceAsync(Build(registration, BoardSetup.From(row, isFirst)));
        }
    }

    private ActiveBoard Build(BoardRegistration registration, BoardSetup setup)
    {
        return registration.Create(registration.Model, setup, _app, _configuration);
    }

    // Only under the gate. Watched by the safety services first, then subscribed, both before it gets a model and before
    // its connection starts: its first state reaches them all.
    private async Task<LiveBoard> AddAsync(Board row)
    {
        var live = new LiveBoard(row.Id, row.Name, _dbFactory, _time, _loggerFactory) { SortOrder = row.SortOrder };
        try
        {
            foreach (var watcher in _watchers)
            {
                await live.OwnAsync(watcher.Watch(live));
            }
        }
        catch
        {
            // The watches taken so far end with it.
            await live.DisposeAsync();
            throw;
        }

        live.StateChanged += OnStateChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                live.StateChanged -= OnStateChanged;
                _ = live.DisposeAsync().AsTask();
                throw new ObjectDisposedException(nameof(BoardRegistry));
            }

            // In the list's order: a new row is last, as it is added with the highest sort order; one made again
            // (RestoreAsync) goes back to its place.
            Volatile.Write(ref _boards, [.. _boards.Append(live).OrderBy(b => b.SortOrder).ThenBy(b => b.BoardId)]);
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
        // A run only ends by itself when it is stopped (or its board removed). One that fails, or ends cancelled while
        // nothing stops it (e.g. a timeout's TaskCanceledException), would leave its board without a connection: it
        // stops the others and fails RunAsync, as one board's did before, so the host stops the app and the container
        // starts it again. A run cancelled by the stop itself stays quiet.
        _ = run.ContinueWith(
            t =>
            {
                // Stopping, or a board taken out of the app (RemoveAsync): its run ending is no failure.
                if (token.IsCancellationRequested || !All.Contains(board))
                {
                    return;
                }

                failed.TrySetException(t.Exception?.InnerExceptions
                    ?? [new OperationCanceledException($"Relay board {board.BoardId}'s connection ended without being stopped.", CancellationOf(t))]);
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // What ended a cancelled run, e.g. which request timed out, for the log; null when it keeps none.
    private static OperationCanceledException? CancellationOf(Task run)
    {
        try
        {
            run.GetAwaiter().GetResult();
            return null;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay board {BoardId} added, a {Model}")]
    private partial void LogBoardAdded(int boardId, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay board {BoardId} removed")]
    private partial void LogRemoved(int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to relay board {BoardId}'s state failed")]
    private partial void LogSubscriberFailed(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay board {BoardId} couldn't be added, and its rows couldn't be deleted again")]
    private partial void LogNotDeleted(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the list of relay boards failed")]
    private partial void LogBoardsListenerFailed(Exception exception);
}
