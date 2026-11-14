namespace Contactor.Web.Persistence;

/// <summary>
/// The installation itself: how people reach the app and sign in, and whether the setup guide has been done. One row;
/// while it doesn't exist every page opens on the setup guide. The board is a <see cref="Board"/>.
/// </summary>
public sealed class Installation
{
    public const int SingletonId = 1;
    public const int MethodTypeMaxLength = 32;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// How people reach the app, by its id in the catalogue of ways in (e.g. <c>cloudflare</c>). Null: not chosen yet.
    /// The migration that made it so gave installations already in use Cloudflare Tunnel, which they all had.
    /// </summary>
    public string? AccessType { get; set; }

    /// <summary>
    /// How people sign in, by its id in the catalogue of sign-in methods (e.g. <c>cloudflare-access</c>). Null: not
    /// chosen yet; the app is then in setup mode. The migration that made it so gave
    /// installations already in use Cloudflare Access, which they all had.
    /// </summary>
    public string? SignInType { get; set; }

    /// <summary>When the setup guide was finished; null while every page should open on it.</summary>
    public DateTime? SetupDoneUtc { get; set; }
}
