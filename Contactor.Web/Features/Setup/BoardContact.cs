using Contactor.Boards;

namespace Contactor.Web.Features.Setup;

/// <summary>
/// Waits for the board to answer, for the setup guide's "Board online" check. Start watching before the address or
/// login is saved: the client reconnects at once, and a watch that starts later can miss the old connection ending.
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

    /// <summary>Starts watching the board's connection.</summary>
    public static BoardContact Watch(IRelayBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return new BoardContact(board);
    }

    /// <summary>
    /// True as soon as the board is connected, false if it isn't within <paramref name="timeout"/>. With
    /// <paramref name="moved"/> (the address or login was changed after the watch started) a connection that was
    /// already open doesn't count: it is to the old address. Only one made after it ended does.
    /// </summary>
    public async Task<bool> WaitAsync(bool moved, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_board.State.IsConnected && (_ended || !moved))
            {
                return true;
            }
        }

        try
        {
            await _reconnected.Task.WaitAsync(timeout, time, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
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
            }
            else if (_ended)
            {
                _reconnected.TrySetResult();
            }
        }
    }
}
