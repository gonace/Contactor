using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// One board's row while the app runs: the <see cref="IRelayBoard"/>, <see cref="IBoardConnection"/> and
/// <see cref="IBoardTraffic"/> of that board, passing everything to its model (<see cref="Active"/>), with its own
/// connection loop (<see cref="RunAsync"/>) and its own Debug trace (<see cref="Trace"/>). The same object for the
/// board's whole life, so a listener stays with it when its model is replaced (<see cref="ReplaceAsync"/>).
/// Before it has a model it has no relays and never answers: commands throw <see cref="BoardNotConnectedException"/>,
/// and nothing connects. A new model's state, unknown until it answers, is reported at once, as for a new address.
/// <see cref="BoardRegistry"/> makes one per board's row and owns it, with what the app runs for the board beside its
/// connection (<see cref="IBoardWatcher"/>), which it disposes first. The changes the app itself just asked this board
/// for are its own (<see cref="Expected"/>).
/// </summary>
public sealed partial class LiveBoard : IRelayBoard, IBoardConnection, IBoardTraffic, IAsyncDisposable
{
    /// <summary>What a board has before its model is chosen: nothing.</summary>
    public static BoardCapabilities NoBoard { get; } = new(RelayCount: 0, InputCount: 0, ReportsChanges: false);

    /// <summary>How often a board without an address looks for one saved since.</summary>
    public static readonly TimeSpan AddressCheck = TimeSpan.FromSeconds(1);

    private const string NotChosen = "No relay board has been chosen yet.";

    /// <summary>What a command to turn a relay on says while the board is being removed (<see cref="Close"/>).</summary>
    public const string BeingRemoved = "The relay board is being removed.";

    private static readonly BoardEndpoint NoEndpoint = new("", 0);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveBoard> _logger;
    private readonly Lock _lock = new();

    // Held while a model's state is checked and passed on, and while the new model's state is raised at a replacement:
    // a state the old model was in the middle of passing on can't come after the new one's.
    private readonly Lock _stateLock = new();
    private TaskCompletionSource _replaced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Slot? _current;
    private string _name;
    private bool _disposed;
    private volatile bool _closed;

    // What watches this board (its safety services' parts), ended first when it is disposed.
    private readonly List<IAsyncDisposable> _watches = [];

    public LiveBoard(int boardId, string name, IDbContextFactory<ContactorDbContext> dbFactory, TimeProvider time, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        BoardId = boardId;
        _name = name;
        _dbFactory = dbFactory;
        _time = time;
        _logger = loggerFactory.CreateLogger<LiveBoard>();

        // Listening from now on, before the first connection.
        Trace = new BoardTrace(this, time, loggerFactory.CreateLogger<BoardTrace>());
        Expected = new ExpectedChanges(time);
    }

    /// <inheritdoc/>
    public event EventHandler<BoardState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The board's row (<see cref="Board.Id"/>).</summary>
    public int BoardId { get; }

    /// <summary>What people call it (<see cref="Board.Name"/>), as of when it was loaded or its model last replaced.</summary>
    public string Name
    {
        get => Volatile.Read(ref _name);
        internal set => Volatile.Write(ref _name, value);
    }

    /// <summary>Where it is in the list (<see cref="Board.SortOrder"/>), as of when it was loaded.</summary>
    public int SortOrder { get; init; }

    /// <summary>The board's model as it runs now, or null before one is chosen.</summary>
    public ActiveBoard? Active => Volatile.Read(ref _current)?.Board;

    /// <summary>The board's model, or null before one is chosen.</summary>
    public BoardModel? Model => Active?.Model;

    /// <summary>
    /// The changes the app itself just asked this board for, by channel: what its status then shows of them isn't a
    /// change outside the app. Only this board's: a channel means a relay on this board.
    /// </summary>
    public ExpectedChanges Expected { get; }

    /// <summary>
    /// The latest steps on this board's connection, for the Debug screen: kept when its model is replaced, and in
    /// memory only.
    /// </summary>
    public BoardTrace Trace { get; }

    public BoardCapabilities Capabilities => Active?.Board.Capabilities ?? NoBoard;

    public BoardState State => Active?.Board.State ?? BoardState.Initial;

    public BoardEndpoint DefaultEndpoint => Active?.Connection.DefaultEndpoint ?? NoEndpoint;

    public BoardLogin DefaultLogin => Active?.Connection.DefaultLogin ?? BoardLogin.None;

    public BoardEndpoint Endpoint => Active?.Connection.Endpoint ?? NoEndpoint;

    public BoardLogin Login => Active?.Connection.Login ?? BoardLogin.None;

    /// <summary>
    /// Whether the board is closed to every new ON while it is being removed (<see cref="Close"/>): a person's tap, a
    /// schedule, an input's action and a group's step all go through here.
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>Throws <see cref="BoardNotConnectedException"/> while the board is closed (<see cref="Close"/>).</summary>
    public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Open().Board.TurnOnAsync(relays, cancellationToken);
    }

    /// <summary>Throws <see cref="BoardNotConnectedException"/> while the board is closed (<see cref="Close"/>).</summary>
    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        return Open().Board.PulseAsync(relays, length, cancellationToken);
    }

    /// <summary>
    /// Closes the board to every new ON, from now until it is disposed or opened again (<see cref="Reopen"/>), while it
    /// is being removed: its relays are turned off and nothing may turn one on again. OFFs still go through. A command
    /// already on its way when it closes isn't stopped; what it did shows in the board's state.
    /// </summary>
    public void Close()
    {
        _closed = true;
    }

    /// <summary>Opens the board again after a removal that didn't go ahead: it works as usual.</summary>
    public void Reopen()
    {
        _closed = false;
    }

    public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Chosen().Board.TurnOffAsync(relays, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return Chosen().Board.RefreshAsync(cancellationToken);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> before a model is chosen: it has no address to change.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        (Active ?? throw new InvalidOperationException(NotChosen)).Connection.SetEndpoint(endpoint);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> before a model is chosen: it has no login to change.</summary>
    public void SetLogin(BoardLogin login)
    {
        (Active ?? throw new InvalidOperationException(NotChosen)).Connection.SetLogin(login);
    }

    public string? CheckLogin(BoardLogin login)
    {
        return Active is { } active ? active.Connection.CheckLogin(login) : NotChosen;
    }

    /// <summary>
    /// Points the model's connection at the address and login saved on the board's row, or the settings' where none
    /// is saved. Done before each model's connection starts; nothing to do before a model is chosen.
    /// </summary>
    public Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        return Active is { } active ? ApplySavedAsync(active, cancellationToken) : Task.CompletedTask;
    }

    private async Task ApplySavedAsync(ActiveBoard active, CancellationToken cancellationToken)
    {
        Board? row;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            row = await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == BoardId, cancellationToken);
        }

        active.UseSaved(BoardSetup.SavedEndpoint(row), BoardSetup.SavedLogin(row));
    }

    /// <summary>
    /// Takes a watch of this board (<see cref="IBoardWatcher.Watch"/>), ended when the board is disposed, before its
    /// model; in the reverse order they were taken. Once disposed, ends it at once.
    /// </summary>
    internal async Task OwnAsync(IAsyncDisposable watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        lock (_lock)
        {
            if (!_disposed)
            {
                _watches.Add(watch);
                return;
            }
        }

        await watch.DisposeAsync();
    }

    /// <summary>
    /// Runs the board until cancelled: for each model, first the address and login saved on its row
    /// (<see cref="ApplySavedAsync"/>), then, once it has an address, what only that model has
    /// (<see cref="ActiveBoard.Extras"/>), then its connection. A board without an address yet (the setup guide asks
    /// for it) waits until one is saved, and tries nothing before. A new model ends the run and starts the new one's;
    /// the old one is disposed once its run has ended. Waits while no model is chosen.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Slot? slot;
            Task replaced;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                slot = _current;
                replaced = _replaced.Task;
                if (slot is not null)
                {
                    slot.Running = true;
                }
            }

            if (slot is null)
            {
                try
                {
                    await replaced.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                await RunOneAsync(slot, cancellationToken);
            }
            finally
            {
                bool dispose;
                lock (_lock)
                {
                    slot.Running = false;
                    dispose = slot.Retired;
                }

                if (dispose)
                {
                    await slot.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="board"/> this board's model, which the live board owns from now on. The model before it
    /// stops: at once if it isn't running, otherwise its run ends and disposes it. Throws for a model built for another
    /// board's row, and once disposed (and disposes <paramref name="board"/> then).
    /// </summary>
    public async Task ReplaceAsync(ActiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.BoardId != BoardId)
        {
            throw new ArgumentException($"The model was built for board {board.BoardId}, not board {BoardId}.", nameof(board));
        }

        var next = new Slot(board);
        Slot? old = null;
        TaskCompletionSource? replaced = null;
        var disposeOld = false;
        bool refused;
        lock (_lock)
        {
            refused = _disposed;
            if (!refused)
            {
                old = _current;
                board.Board.StateChanged += OnStateChanged;
                board.Traffic.Traced += OnTraced;
                Volatile.Write(ref _current, next);
                if (old is not null)
                {
                    old.Board.Board.StateChanged -= OnStateChanged;
                    old.Board.Traffic.Traced -= OnTraced;
                    old.Retired = true;
                }

                disposeOld = old is { Running: false };
                replaced = _replaced;
                _replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (refused)
        {
            await next.DisposeAsync();
            throw new ObjectDisposedException(nameof(LiveBoard));
        }

        LogReplaced(BoardId, board.Model.Name, old?.Board.Model.Name ?? "none");

        // Outside the lock: cancelling runs the old connection's callbacks.
        old?.Retire();
        replaced!.TrySetResult();
        lock (_stateLock)
        {
            Raise(board.Board.State);
        }

        if (disposeOld)
        {
            await old!.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Slot? slot;
        bool dispose;
        IAsyncDisposable[] watches;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watches = [.. Enumerable.Reverse(_watches)];
            _watches.Clear();
            slot = _current;
            Volatile.Write(ref _current, null);

            // A run waiting for a model ends.
            _replaced.TrySetResult();
            if (slot is null)
            {
                dispose = false;
            }
            else
            {
                slot.Board.Board.StateChanged -= OnStateChanged;
                slot.Board.Traffic.Traced -= OnTraced;
                slot.Retired = true;
                dispose = !slot.Running;
            }
        }

        // What watches the board first: nothing acts on it once its connection ends.
        foreach (var watch in watches)
        {
            try
            {
                await watch.DisposeAsync();
            }
#pragma warning disable CA1031 // One watch that fails to end must not keep the others or the connection going.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWatchNotEnded(ex, BoardId);
            }
        }

        Trace.Dispose();
        if (slot is null)
        {
            return;
        }

        slot.Retire();
        if (dispose)
        {
            await slot.DisposeAsync();
        }
    }

    private ActiveBoard Chosen()
    {
        return Active ?? throw new BoardNotConnectedException(NotChosen);
    }

    private ActiveBoard Open()
    {
        return _closed ? throw new BoardNotConnectedException(BeingRemoved) : Chosen();
    }

    private async Task RunOneAsync(Slot slot, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, slot.Token);
        var token = linked.Token;
        var started = new List<IHostedService>();
        try
        {
            await ApplySavedAsync(slot.Board, token);
            while (!slot.Board.Connection.Endpoint.HasHost)
            {
                await Task.Delay(AddressCheck, _time, token);
            }

            foreach (var extra in slot.Board.Extras)
            {
                await extra.StartAsync(token);
                started.Add(extra);
            }

            await slot.Board.Connection.RunAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping, or the model was replaced.
        }
        finally
        {
            started.Reverse();
            foreach (var extra in started)
            {
                try
                {
                    await extra.StopAsync(CancellationToken.None);
                }
#pragma warning disable CA1031 // One that fails to stop must not keep the others running.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogExtraNotStopped(ex, extra.GetType().Name, BoardId);
                }
            }
        }
    }

    // Only the current model's: one replaced may still be in the middle of telling.
    private void OnStateChanged(object? sender, BoardState state)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(sender, Active?.Board))
            {
                Raise(state);
            }
        }
    }

    // A step the old model was already passing on may still come just after a replacement: it did happen, and the
    // Debug screen only lists it.
    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        if (ReferenceEquals(sender, Active?.Traffic))
        {
            Traced?.Invoke(this, entry);
        }
    }

    // Each subscriber on its own, as the boards do: one that fails must not keep the state from the others.
    private void Raise(BoardState state)
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try
            {
                handler(this, state);
            }
#pragma warning disable CA1031 // A subscriber's failure is logged; the others still get the state.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(ex, BoardId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay board {BoardId} is now the {Chosen} (before: {Before})")]
    private partial void LogReplaced(int boardId, string chosen, string before);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} of relay board {BoardId} didn't stop cleanly")]
    private partial void LogExtraNotStopped(Exception exception, string service, int boardId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A watch of relay board {BoardId} didn't end cleanly")]
    private partial void LogWatchNotEnded(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to relay board {BoardId}'s state failed")]
    private partial void LogSubscriberFailed(Exception exception, int boardId);

    // A model while it is the board's, or until its run has ended after it was replaced.
    private sealed class Slot : IAsyncDisposable
    {
        private readonly CancellationTokenSource _retired = new();

        public Slot(ActiveBoard board)
        {
            Board = board;
            Token = _retired.Token;
        }

        public ActiveBoard Board { get; }

        public CancellationToken Token { get; }

        // Both only under the live board's lock.
        public bool Running { get; set; }

        public bool Retired { get; set; }

        public void Retire()
        {
            _retired.Cancel();
        }

        // The token source is never disposed: Retire may come after the run already disposed the board, and a
        // source without a timer holds nothing that needs it.
        public ValueTask DisposeAsync()
        {
            return Board.DisposeAsync();
        }
    }
}
