namespace Contactor.Boards;

/// <summary>
/// A command could not be sent because the board is not connected. Commands are never queued for later. Not sealed: the
/// app tells a board it is removing apart (a board closed to new ONs).
/// </summary>
public class BoardNotConnectedException : Exception
{
    public BoardNotConnectedException()
        : base("The relay board is not connected.")
    {
    }

    public BoardNotConnectedException(string message)
        : base(message)
    {
    }

    public BoardNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
