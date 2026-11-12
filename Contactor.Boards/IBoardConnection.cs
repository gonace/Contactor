namespace Contactor.Boards;

/// <summary>
/// The connection to a board on the network: where it is and how to log in, which can change while the app runs.
/// The app keeps it running with <see cref="RunAsync"/> for its lifetime.
/// </summary>
public interface IBoardConnection
{
    /// <summary>The address from the app's settings, which applies until one is saved in the app.</summary>
    BoardEndpoint DefaultEndpoint { get; }

    /// <summary>The login from the app's settings, which applies until one is saved in the app.</summary>
    BoardLogin DefaultLogin { get; }

    /// <summary>The address in use.</summary>
    BoardEndpoint Endpoint { get; }

    /// <summary>The login in use.</summary>
    BoardLogin Login { get; }

    /// <summary>
    /// Moves the connection to another address at once: the current connection, attempt or wait ends. Another board
    /// may be there, so the board's state is forgotten at once, as at start (not connected, and no
    /// <see cref="BoardState.LastContactUtc"/>), and an answer from the old address still on its way is dropped.
    /// </summary>
    void SetEndpoint(BoardEndpoint endpoint);

    /// <summary>
    /// Logs in with another login at once. Throws <see cref="ArgumentException"/> for a login
    /// <see cref="CheckLogin"/> refuses.
    /// </summary>
    void SetLogin(BoardLogin login);

    /// <summary>Why the board can't take this login, in words for the person who typed it, or null when it can.</summary>
    string? CheckLogin(BoardLogin login);

    /// <summary>Connects, and reconnects whenever the connection is lost, until cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
