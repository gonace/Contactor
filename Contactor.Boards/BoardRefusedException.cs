namespace Contactor.Boards;

/// <summary>
/// The board answered, but didn't take the command: it refused every relay in it, in an answer the app could read.
/// <see cref="BoardPoller{TReading}"/> doesn't count it toward losing the board, as the board answered.
/// </summary>
public sealed class BoardRefusedException : Exception
{
    public BoardRefusedException()
        : base("The relay board answered, but didn't take the command.")
    {
    }

    public BoardRefusedException(string message)
        : base(message)
    {
    }

    public BoardRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
