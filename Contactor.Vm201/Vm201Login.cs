namespace Contactor.Vm201;

/// <summary>
/// The board's login: empty user name and password for a board without one. A class rather than a record, so the
/// password can never end up in a log through a generated ToString.
/// </summary>
public sealed class Vm201Login : IEquatable<Vm201Login>
{
    public static Vm201Login None { get; } = new("", "");

    private Vm201Login(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    /// <summary>A secret: never log it.</summary>
    public string Password { get; }

    public bool IsNone => Username.Length == 0 && Password.Length == 0;

    /// <summary>A login from what someone typed, or null with the reason (in Swedish) when the board can't take it.</summary>
    public static Vm201Login? TryCreate(string? username, string? password, out string? error)
    {
        username ??= "";
        password ??= "";
        foreach (var (value, what) in new[] { (username, "Användarnamnet"), (password, "Lösenordet") })
        {
            if (value.Length > Vm201Command.CredentialMaxLength)
            {
                error = $"{what} får vara högst {Vm201Command.CredentialMaxLength} tecken på reläkortet.";
                return null;
            }

            if (value.Any(c => c is < (char)0x20 or > (char)0x7E))
            {
                error = $"{what} får bara innehålla bokstäver a–z, siffror och vanliga tecken (inga å, ä, ö).";
                return null;
            }
        }

        error = null;
        return new Vm201Login(username, password);
    }

    public bool Equals(Vm201Login? other)
    {
        return other is not null && Username == other.Username && Password == other.Password;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as Vm201Login);
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
