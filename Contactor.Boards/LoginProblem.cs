namespace Contactor.Boards;

/// <summary>What a login check found wrong; the app puts it in words, in its own language.</summary>
public enum LoginProblemKind
{
    /// <summary>The user name or the password is longer than the board takes.</summary>
    TooLong,

    /// <summary>The user name or the password has a character other than letters a–z, digits and plain symbols.</summary>
    PlainCharacters,

    /// <summary>The user name or the password has a character other than letters a–z, digits and the listed symbols.</summary>
    ListedCharacters,

    /// <summary>The user name and the password together are longer than the board takes.</summary>
    TooLongTogether,

    /// <summary>The board takes a password only, and a user name was given.</summary>
    PasswordOnly,

    /// <summary>The password alone is longer than the board takes.</summary>
    PasswordTooLong,

    /// <summary>The password has a character other than letters, digits and plain symbols, or a space.</summary>
    PasswordCharacters,

    /// <summary>The board takes no login: its password only protects its own web pages.</summary>
    NoLoginWebPages,

    /// <summary>The board takes no login: its password only protects its own web page.</summary>
    NoLoginWebPage,
}

/// <summary>Which part of a login a <see cref="LoginProblem"/> is about.</summary>
public enum LoginField
{
    UserName,
    Password,
}

/// <summary>
/// Why a board can't take a login (<see cref="IBoardConnection.CheckLogin"/>). The app puts it in words for the
/// person who typed it; <see cref="ToString"/> is the English for exceptions, logs and the simulator.
/// </summary>
/// <param name="MaxLength">The longest the board takes, for the "too long" kinds.</param>
/// <param name="Symbols">The symbols the board takes, for <see cref="LoginProblemKind.ListedCharacters"/>.</param>
/// <param name="Board">How the board is named, for the kinds that name it, e.g. "VM208" or "Devantech board".</param>
public sealed record LoginProblem(LoginProblemKind Kind, LoginField Field = LoginField.Password, int MaxLength = 0, string Symbols = "", string Board = "")
{
    public override string ToString()
    {
        var what = Field == LoginField.UserName ? "The user name" : "The password";
        return Kind switch
        {
            LoginProblemKind.TooLong => $"{what} can be at most {MaxLength} characters on the relay board.",
            LoginProblemKind.PlainCharacters => $"{what} can only contain letters a–z, digits and plain symbols.",
            LoginProblemKind.ListedCharacters => $"{what} can only contain letters a–z, digits and {Symbols} on the relay board.",
            LoginProblemKind.TooLongTogether => $"The user name and password can be at most {MaxLength} characters together on the relay board.",
            LoginProblemKind.PasswordOnly => $"The {Board} takes a password only, no user name.",
            LoginProblemKind.PasswordTooLong => $"The password can be at most {MaxLength} characters.",
            LoginProblemKind.PasswordCharacters => "The password can only contain letters a–z and A–Z, digits and plain symbols, without spaces.",
            LoginProblemKind.NoLoginWebPages => $"The {Board} takes no login: its password only protects its own web pages.",
            _ => $"The {Board} takes no login: its password only protects its own web page.",
        };
    }
}

/// <summary>What a board's user name and password may contain, for the screens' hint under the login.</summary>
public enum LoginHintKind
{
    /// <summary>At most <see cref="LoginHint.MaxLength"/> characters: letters a–z, digits and plain symbols.</summary>
    PlainSymbols,

    /// <summary>
    /// At most <see cref="LoginHint.MaxLength"/> characters, <see cref="LoginHint.TotalMaxLength"/> for both together:
    /// letters a–z, digits and <see cref="LoginHint.Symbols"/>.
    /// </summary>
    ListedSymbols,

    /// <summary>
    /// The TCP password from the board's configuration page, at most <see cref="LoginHint.MaxLength"/> characters:
    /// letters, digits and plain symbols, no spaces.
    /// </summary>
    TcpPassword,
}

/// <summary>A model's rule for its login, which the app puts in words for the Settings screen and the setup guide.</summary>
public sealed record LoginHint(LoginHintKind Kind, int MaxLength, int TotalMaxLength = 0, string Symbols = "");
