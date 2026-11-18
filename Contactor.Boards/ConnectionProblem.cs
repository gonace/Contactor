namespace Contactor.Boards;

/// <summary>Why a board refused the app's connection on purpose; the app puts it in words, in its own language.</summary>
public enum ConnectionProblemKind
{
    /// <summary>Another model of board is at the address.</summary>
    WrongModel,

    /// <summary>The board asks for a login (or a password) and the app has none.</summary>
    LoginNeeded,

    /// <summary>The board refused the login (or the password) the app sent.</summary>
    LoginRefused,

    /// <summary>
    /// The board's firmware is older than the app takes; <see cref="ConnectionProblem.MinFirmware"/> is the oldest it
    /// takes, when known.
    /// </summary>
    FirmwareTooOld,

    /// <summary>
    /// The board asks the app to wait before it tries again, after too many wrong logins (or passwords). The app tries
    /// again by itself, never in a quick loop.
    /// </summary>
    TooManyTries,
}

/// <summary>
/// Why the app can't connect to a board that answered, so the screens can say more than "no answer". Cleared at the
/// board's first answer, at a new address and at a new login. Holds no secret: never a user name or a password.
/// <see cref="ToString"/> is the English for logs and tests.
/// </summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Expected">The model the app was set up for.</param>
/// <param name="Found">
/// For <see cref="ConnectionProblemKind.WrongModel"/>, the model in the board catalogue that is at the address; null
/// when it is a model the app doesn't support, or doesn't know.
/// </param>
/// <param name="MinFirmware">
/// For <see cref="ConnectionProblemKind.FirmwareTooOld"/>, the oldest firmware version the app takes, e.g. "1.7.0"; null
/// when the board doesn't say.
/// </param>
public sealed record ConnectionProblem(ConnectionProblemKind Kind, BoardModel Expected, BoardModel? Found = null, string? MinFirmware = null)
{
    /// <summary>
    /// Whether it is about the login (or the password): needed, refused, or too many wrong ones. The screens then open
    /// the login fields.
    /// </summary>
    public bool IsAboutLogin => Kind is ConnectionProblemKind.LoginNeeded or ConnectionProblemKind.LoginRefused or ConnectionProblemKind.TooManyTries;

    public override string ToString()
    {
        return Kind switch
        {
            ConnectionProblemKind.WrongModel when Found is not null => $"The board at this address is an {Found.ModelName}, not an {Expected.ModelName}.",
            ConnectionProblemKind.WrongModel => $"The board at this address is another model, not an {Expected.ModelName}.",
            ConnectionProblemKind.LoginNeeded => Expected.PasswordOnly
                ? $"The {Expected.ModelName} asks for a password."
                : $"The {Expected.ModelName} asks for a login.",
            ConnectionProblemKind.FirmwareTooOld => MinFirmware is { } min
                ? $"The board's firmware is too old: the app needs {min} or later."
                : "The board's firmware is too old.",
            ConnectionProblemKind.TooManyTries => Expected.PasswordOnly
                ? "The board asks to wait after too many wrong passwords."
                : "The board asks to wait after too many wrong logins.",
            _ => Expected.PasswordOnly
                ? $"The {Expected.ModelName} refused the password."
                : $"The {Expected.ModelName} refused the login.",
        };
    }
}

/// <summary>
/// An exception that says why a board refused the connection on purpose. <see cref="BoardPoller{TReading}"/> reads it
/// from a failed connect and puts it on the state.
/// </summary>
public interface IConnectionProblemSource
{
    /// <summary>Why the board refused the connection, or null when it doesn't say.</summary>
    ConnectionProblem? Problem { get; }
}
