using System.Globalization;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// How e-mail addresses become users.
/// </summary>
public static class IdentityRules
{
    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>Something before and after one @, and short enough to store.</summary>
    public static bool IsValidEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return email.Length <= User.EmailMaxLength
            && at > 0
            && at == email.LastIndexOf('@')
            && at < email.Length - 1
            && !email.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// The part before @ with a capital first letter, e.g. <c>nils@example.com</c> → "Nils", cut to the stored
    /// length.
    /// </summary>
    public static string DisplayNameFor(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var local = email[..email.IndexOf('@', StringComparison.Ordinal)];
        var name = char.ToUpper(local[0], CultureInfo.InvariantCulture) + local[1..];
        return name.Length <= User.DisplayNameMaxLength ? name : name[..User.DisplayNameMaxLength];
    }
}
