using Contactor.Boards;

namespace Contactor.Web.Features.Setup;

/// <summary>What <see cref="BoardContact.WaitAsync"/> found: the board connected, refused the connection, or no answer.</summary>
/// <param name="IsConnected">The board answered.</param>
/// <param name="Problem">Why the board refuses the connection; null when it answered or didn't answer at all.</param>
public sealed record BoardContactResult(bool IsConnected, ConnectionProblem? Problem)
{
    public static BoardContactResult Connected { get; } = new(true, null);

    public static BoardContactResult NoAnswer { get; } = new(false, null);
}

/// <summary>
/// Waits for one board to answer (its <see cref="Boards.LiveBoard"/>, found by its row id), for the setup guide's "Board
/// online" check. Start watching before the address or login is saved: the client reconnects at once, and a watch that
/// starts later can miss the old connection ending.
/// </summary>
public sealed class BoardContact : IDisposable
{
    /// <summary>How long the guide waits for the board before it says it found none.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IRelayBoard _board;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Whether a connection open when the watch started has ended since (or there was none).
    private bool _ended;

    // The wait under way, told when the board refuses the connection; null while none waits.
    private TaskCompletionSource<ConnectionProblem>? _refused;

    private BoardContact(IRelayBoard board)
    {
        _board = board;
        _board.StateChanged += OnChanged;
        lock (_gate)
        {
            _ended = !board.State.IsConnected;
        }
    }

    /// <summary>
    /// Whether the connection now goes somewhere else than <paramref name="endpoint"/> with <paramref name="login"/>,
    /// by value: the connection hands out a new login object each time.
    /// </summary>
    public static bool HasMoved(IBoardConnection connection, BoardEndpoint endpoint, BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return !connection.Endpoint.Equals(endpoint) || !connection.Login.Equals(login);
    }

    /// <summary>
    /// "Check again": asks the board for its status and returns true once it answers, false if it doesn't within
    /// <paramref name="timeout"/> or the app has no contact with it. An answer is a status after the request, or a
    /// later <see cref="BoardState.LastContactUtc"/> (a polled board reports only changes).
    /// </summary>
    public static async Task<bool> AskAsync(IRelayBoard board, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(time);
        var before = board.State.LastContactUtc;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, BoardState state)
        {
            if (state.IsConnected)
            {
                answered.TrySetResult();
            }
        }

        board.StateChanged += OnChanged;
        try
        {
            await board.RefreshAsync(cancellationToken);
            var deadline = time.GetUtcNow() + timeout;
            while (true)
            {
                var state = board.State;
                if (answered.Task.IsCompleted || (state.IsConnected && state.LastContactUtc > before))
                {
                    return true;
                }

                if (time.GetUtcNow() >= deadline)
                {
                    return false;
                }

                await Task.WhenAny(answered.Task, Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken));
            }
        }
        catch (BoardNotConnectedException)
        {
            return false;
        }
        finally
        {
            board.StateChanged -= OnChanged;
        }
    }

    /// <summary>Starts watching the board's connection.</summary>
    public static BoardContact Watch(IRelayBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return new BoardContact(board);
    }

    /// <summary>
    /// Connected as soon as the board is, the problem as soon as the board refuses the connection (e.g. another model or
    /// a wrong password), and no answer if neither comes within <paramref name="timeout"/>. With
    /// <paramref name="moved"/> (the address or login was changed after the watch started) a connection that was
    /// already open doesn't count: it is to the old address. Only one made after it ended does. A problem from before
    /// a move doesn't count either: the board's connection clears it when it moves, and drops one still on its way.
    /// </summary>
    public async Task<BoardContactResult> WaitAsync(bool moved, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        var refused = new TaskCompletionSource<ConnectionProblem>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var state = _board.State;
            if (state.IsConnected && (_ended || !moved))
            {
                return BoardContactResult.Connected;
            }

            // A problem the board reported already is the answer at once. That holds after a move too, only because the
            // board's connection clears its problem synchronously in SetEndpoint and SetLogin, before the save returns:
            // what is left is about the new address and login. "Check again" with nothing changed clears it the same way
            // (TryAgain) before it waits.
            if (!state.IsConnected && state.Problem is { } problem)
            {
                return new BoardContactResult(false, problem);
            }

            _refused = refused;
        }

        try
        {
            var first = await Task.WhenAny(_reconnected.Task, refused.Task).WaitAsync(timeout, time, cancellationToken);
            return first == _reconnected.Task ? BoardContactResult.Connected : new BoardContactResult(false, await refused.Task);
        }
        catch (TimeoutException)
        {
            return BoardContactResult.NoAnswer;
        }
        finally
        {
            lock (_gate)
            {
                _refused = null;
            }
        }
    }

    public void Dispose()
    {
        _board.StateChanged -= OnChanged;
    }

    private void OnChanged(object? sender, BoardState state)
    {
        lock (_gate)
        {
            if (!state.IsConnected)
            {
                _ended = true;
                if (state.Problem is { } problem)
                {
                    _refused?.TrySetResult(problem);
                }
            }
            else if (_ended)
            {
                _reconnected.TrySetResult();
            }
        }
    }
}
