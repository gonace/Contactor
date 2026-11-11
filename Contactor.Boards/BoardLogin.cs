namespace Contactor.Boards;

/// <summary>
/// A board's login: empty user name and password for a board without one. Which values a board takes is its own
/// rule (<see cref="IBoardConnection.CheckLogin"/>). A class rather than a record, so the password can never end up in
/// a log through a generated ToString.
/// </summary>
public sealed class BoardLogin : IEquatable<BoardLogin>
{
    public BoardLogin(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        Username = username;
        Password = password;
    }

    /// <summary>The longest user name or password the app stores, for any board; a model's own limit is lower.</summary>
    public const int MaxLength = 64;

    public static BoardLogin None { get; } = new("", "");

    public string Username { get; }

    /// <summary>A secret: never log it.</summary>
    public string Password { get; }

    public bool IsNone => Username.Length == 0 && Password.Length == 0;

    public bool Equals(BoardLogin? other)
    {
        return other is not null && Username == other.Username && Password == other.Password;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as BoardLogin);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Username, Password);
    }

    public override string ToString()
    {
        return IsNone ? "no login" : $"login as {Username}";
    }
}
