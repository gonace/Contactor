using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// The relay board as the rest of the app sees it: the one <see cref="IRelayBoard"/>, <see cref="IBoardConnection"/>
/// and <see cref="IBoardTraffic"/>, passing everything to the chosen board. Before a board is chosen it has no relays
/// and never answers: commands throw <see cref="BoardNotConnectedException"/>, and nothing connects. Switching to
/// another board (<see cref="SwitchToAsync"/>) happens while the app runs: the old board's connection stops, and the
/// new board's state, unknown until it answers, is reported as a new state at once, as for a new address.
/// </summary>
public sealed partial class BoardSwitch : IRelayBoard, IBoardConnection, IBoardTraffic, IAsyncDisposable
{
    /// <summary>What the app has before a board is chosen: nothing.</summary>
    public static BoardCapabilities NoBoard { get; } = new(RelayCount: 0, InputCount: 0, ReportsChanges: false);

    private const string NotChosen = "No relay board has been chosen yet.";

    private static readonly BoardEndpoint NoEndpoint = new("", 0);

    private readonly Lock _lock = new();
    private readonly ILogger<BoardSwitch> _logger;
    private TaskCompletionSource _switched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Slot? _current;

    public BoardSwitch(ILogger<BoardSwitch> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<BoardState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The chosen board, or null before one is chosen.</summary>
    public ActiveBoard? Current => Volatile.Read(ref _current)?.Board;

    /// <summary>The chosen board's model, or null before one is chosen.</summary>
    public BoardModel? Model => Current?.Model;

    public BoardCapabilities Capabilities => Current?.Board.Capabilities ?? NoBoard;

    public BoardState State => Current?.Board.State ?? BoardState.Initial;

    public BoardEndpoint DefaultEndpoint => Current?.Connection.DefaultEndpoint ?? NoEndpoint;

    public BoardLogin DefaultLogin => Current?.Connection.DefaultLogin ?? BoardLogin.None;

    public BoardEndpoint Endpoint => Current?.Connection.Endpoint ?? NoEndpoint;

    public BoardLogin Login => Current?.Connection.Login ?? BoardLogin.None;

    public Task TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Chosen().Board.TurnOnAsync(relays, cancellationToken);
    }

    public Task TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Chosen().Board.TurnOffAsync(relays, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return Chosen().Board.RefreshAsync(cancellationToken);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> before a board is chosen: it has no address to change.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        (Current ?? throw new InvalidOperationException(NotChosen)).Connection.SetEndpoint(endpoint);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> before a board is chosen: it has no login to change.</summary>
    public void SetLogin(BoardLogin login)
    {
        (Current ?? throw new InvalidOperationException(NotChosen)).Connection.SetLogin(login);
    }

    public string? CheckLogin(BoardLogin login)
    {
        return Current is { } current ? current.Connection.CheckLogin(login) : NotChosen;
    }

    /// <summary>Runs the chosen board's connection until cancelled, and the next one's after a switch.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return RunAsync(prepare: null, cancellationToken);
    }

    /// <summary>
    /// Runs the chosen board until cancelled: first <paramref name="prepare"/> (e.g. its saved address), then what
    /// only it has (<see cref="ActiveBoard.Extras"/>), then its connection. A switch ends the run and starts the new
    /// board's; the old one is disposed once its run has ended. Waits while no board is chosen.
    /// </summary>
    public async Task RunAsync(Func<ActiveBoard, CancellationToken, Task>? prepare, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Slot? slot;
            Task switched;
            lock (_lock)
            {
                slot = _current;
                switched = _switched.Task;
                if (slot is not null)
                {
                    slot.Running = true;
                }
            }

            if (slot is null)
            {
                try
                {
                    await switched.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                await RunOneAsync(slot, prepare, cancellationToken);
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
    /// Makes <paramref name="board"/> the chosen board, which the switch owns from now on. The board before it stops:
    /// at once if it isn't running, otherwise its run ends and disposes it.
    /// </summary>
    public async Task SwitchToAsync(ActiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var next = new Slot(board);
        Slot? old;
        TaskCompletionSource switched;
        bool disposeOld;
        lock (_lock)
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
            switched = _switched;
            _switched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        LogSwitched(board.Model.Name, old?.Board.Model.Name ?? "none");

        // Outside the lock: cancelling runs the old connection's callbacks.
        old?.Retire();
        switched.TrySetResult();
        Raise(board.Board.State);
        if (disposeOld)
        {
            await old!.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Slot? slot;
        bool dispose;
        lock (_lock)
        {
            slot = _current;
            Volatile.Write(ref _current, null);
            if (slot is null)
            {
                return;
            }

            slot.Board.Board.StateChanged -= OnStateChanged;
            slot.Board.Traffic.Traced -= OnTraced;
            slot.Retired = true;
            dispose = !slot.Running;
        }

        slot.Retire();
        if (dispose)
        {
            await slot.DisposeAsync();
        }
    }

    private ActiveBoard Chosen()
    {
        return Current ?? throw new BoardNotConnectedException(NotChosen);
    }

    private async Task RunOneAsync(Slot slot, Func<ActiveBoard, CancellationToken, Task>? prepare, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, slot.Token);
        var token = linked.Token;
        var started = new List<IHostedService>();
        try
        {
            if (prepare is not null)
            {
                await prepare(slot.Board, token);
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
            // Stopping, or switched to another board.
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
                    LogExtraNotStopped(ex, extra.GetType().Name);
                }
            }
        }
    }

    // Only the chosen board's: one switched away from may still be in the middle of telling.
    private void OnStateChanged(object? sender, BoardState state)
    {
        if (ReferenceEquals(sender, Current?.Board))
        {
            Raise(state);
        }
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        if (ReferenceEquals(sender, Current?.Traffic))
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
                LogSubscriberFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The relay board is now the {Chosen} (before: {Before})")]
    private partial void LogSwitched(string chosen, string before);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} didn't stop cleanly")]
    private partial void LogExtraNotStopped(Exception exception, string service);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the relay board's state failed")]
    private partial void LogSubscriberFailed(Exception exception);

    // A board while it is the chosen one, or until its run has ended after it was switched away from.
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

        // Both only under the switch's lock.
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
