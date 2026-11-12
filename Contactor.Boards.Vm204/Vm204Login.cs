namespace Contactor.Boards.Vm204;

/// <summary>
/// The board's API login. A class rather than a record, so the password can never end up in a log through a generated
/// ToString. Empty user name and password for a board whose login was cleared.
/// </summary>
public sealed class Vm204Login : IEquatable<Vm204Login>
{
    public static Vm204Login None { get; } = new("", "");

    private Vm204Login(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    /// <summary>A secret: never log it.</summary>
    public string Password { get; }

    public bool IsNone => Username.Length == 0 && Password.Length == 0;

    /// <summary>A login from what someone typed, or null with the reason when the board can't take it.</summary>
    public static Vm204Login? TryCreate(string? username, string? password, out string? error)
    {
        username ??= "";
        password ??= "";
        foreach (var (value, what) in new[] { (username, "The user name"), (password, "The password") })
        {
            if (value.Length > Vm204Api.CredentialMaxLength)
            {
                error = $"{what} can be at most {Vm204Api.CredentialMaxLength} characters on the relay board.";
                return null;
            }

            if (!value.All(Vm204Api.IsCredentialCharacter))
            {
                error = $"{what} can only contain letters a–z, digits and {SymbolList} on the relay board.";
                return null;
            }
        }

        if (username.Length + password.Length > Vm204Api.CredentialsTotalMaxLength)
        {
            error = $"The user name and password can be at most {Vm204Api.CredentialsTotalMaxLength} characters together on the relay board.";
            return null;
        }

        error = null;
        return new Vm204Login(username, password);
    }

    /// <summary>The allowed symbols with spaces between them, for people: "- . _ ~ ! $ ' ( ) * + , ; : @".</summary>
    public static string SymbolList => string.Join(' ', Vm204Api.CredentialSymbols.ToCharArray());

    public bool Equals(Vm204Login? other)
    {
        return other is not null && Username == other.Username && Password == other.Password;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as Vm204Login);
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
