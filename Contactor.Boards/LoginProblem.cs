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

    /// <summary>The board takes a password only, and a user name was given: "The <c>Board</c> board takes …".</summary>
    PasswordOnly,

    /// <summary>The password alone is longer than the board takes.</summary>
    PasswordTooLong,

    /// <summary>The password has a character other than letters, digits and plain symbols, or a space.</summary>
    PasswordCharacters,

    /// <summary>The board takes no login: its password only protects its own web pages. "The <c>Board</c> takes …".</summary>
    NoLoginWebPages,

    /// <summary>The board takes no login: its password only protects its own web page. "The <c>Board</c> board takes …".</summary>
    NoLoginWebPage,

    /// <summary>
    /// The password, an SNMP community, is shorter than <see cref="LoginProblem.MinLength"/> or longer than
    /// <see cref="LoginProblem.MaxLength"/> characters; empty included, as the board always has one.
    /// </summary>
    CommunityLength,
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
/// <param name="Board">
/// The board's brand or model, a proper noun that every language keeps as it is, for the kinds that name it, e.g. "VM208"
/// or "Devantech"; the word "board" is in the sentence.
/// </param>
/// <param name="MinLength">The shortest the board takes, for <see cref="LoginProblemKind.CommunityLength"/>.</param>
public sealed record LoginProblem(LoginProblemKind Kind, LoginField Field = LoginField.Password, int MaxLength = 0, string Symbols = "", string Board = "", int MinLength = 0)
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
            LoginProblemKind.PasswordOnly => $"The {Board} board takes a password only, no user name.",
            LoginProblemKind.PasswordTooLong => $"The password can be at most {MaxLength} characters.",
            LoginProblemKind.PasswordCharacters => "The password can only contain letters a–z and A–Z, digits and plain symbols, without spaces.",
            LoginProblemKind.NoLoginWebPages => $"The {Board} takes no login: its password only protects its own web pages.",
            LoginProblemKind.CommunityLength => $"The password, the board's read-write community, must be {MinLength} to {MaxLength} characters.",
            _ => $"The {Board} board takes no login: its password only protects its own web page.",
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

    /// <summary>
    /// The read-write community from the board's SNMP settings, <see cref="LoginHint.MinLength"/> to
    /// <see cref="LoginHint.MaxLength"/> characters: letters, digits and plain symbols, no spaces; on a new board
    /// <see cref="LoginHint.FactoryValue"/>. A wrong community gets no answer at all, so the hint says to check the
    /// address and the community.
    /// </summary>
    SnmpCommunity,
}

/// <summary>A model's rule for its login, which the app puts in words for the Settings screen and the setup guide.</summary>
/// <param name="MinLength">The shortest it takes, for <see cref="LoginHintKind.SnmpCommunity"/>.</param>
/// <param name="FactoryValue">
/// What a new board has, from its manual and not a secret, for <see cref="LoginHintKind.SnmpCommunity"/>, e.g. "private".
/// </param>
public sealed record LoginHint(LoginHintKind Kind, int MaxLength, int TotalMaxLength = 0, string Symbols = "", int MinLength = 0, string FactoryValue = "");
