using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// An ON refused because the board is being removed (<see cref="LiveBoard.CloseAsync"/>): nothing was sent. Unlike a lost
/// board, the board still answers; should the removal not go ahead (<see cref="LiveBoard.Reopen"/>), what was refused
/// can be done then: a schedule's ON at its next tick, an input's action if the input is still as it was.
/// </summary>
public sealed class BoardClosedException : BoardNotConnectedException
{
    public BoardClosedException()
        : base(LiveBoard.BeingRemoved)
    {
    }

    public BoardClosedException(string message)
        : base(message)
    {
    }

    public BoardClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
