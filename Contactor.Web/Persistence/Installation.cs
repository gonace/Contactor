namespace Contactor.Web.Persistence;

/// <summary>
/// The installation itself: which model of board it has, how people reach the app and sign in, and whether the setup
/// guide has been done. One row; while it doesn't exist there is no board, and every page opens on the setup guide.
/// </summary>
public sealed class Installation
{
    public const int SingletonId = 1;
    public const int BoardTypeMaxLength = 32;
    public const int MethodTypeMaxLength = 32;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The model chosen in the setup guide, by its id in the board catalogue (e.g. <c>vm201</c>). Null while none is
    /// chosen: the app then has no board.
    /// </summary>
    public string? BoardType { get; set; }

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
