namespace Contactor.Simulator;

/// <summary>
/// The page's fields for changing a running board's login: what is typed and the error shown. They belong to the board
/// the page shows, so they start afresh whenever it shows another.
/// </summary>
public sealed class LoginForm
{
    /// <summary>The board the fields are for; null when none is shown.</summary>
    public int? BoardId { get; private set; }

    public string Username { get; set; } = "";

    /// <summary>As typed; cleared once the board takes it, and never shown anywhere but in its own field.</summary>
    public string Password { get; set; } = "";

    public string? Error { get; private set; }

    /// <summary>Starts the fields afresh when <paramref name="boardId"/> is another board than the one they are for.</summary>
    public void Follow(int? boardId)
    {
        if (boardId == BoardId)
        {
            return;
        }

        BoardId = boardId;
        Username = "";
        Password = "";
        Error = null;
    }

    /// <summary>Changes the board's login to the one typed; the fields are cleared once it takes it.</summary>
    public void Change(RunningBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        Error = board.ChangeLogin(Username, Password);
        if (Error is null)
        {
            Username = "";
            Password = "";
        }
    }
}
