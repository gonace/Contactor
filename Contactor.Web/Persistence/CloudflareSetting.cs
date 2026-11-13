namespace Contactor.Web.Persistence;

/// <summary>
/// Cloudflare's settings as saved in the app (the setup guide): the tunnel and Cloudflare Access. One row. A setting
/// left empty here comes from the app's settings (<c>Tunnel:Token</c>, <c>CloudflareAccess:TeamDomain</c> and
/// <c>CloudflareAccess:Audience</c>, e.g. from <c>.env</c>), as before there was a row.
/// </summary>
public sealed class CloudflareSetting
{
    public const int SingletonId = 1;
    public const int TunnelTokenMaxLength = 4096;
    public const int HostNameMaxLength = 253;
    public const int AudienceMaxLength = 128;

    public int Id { get; set; } = SingletonId;

    /// <summary>The tunnel's token. A secret: never logged or shown, and given to cloudflared only through its environment.</summary>
    public string? TunnelToken { get; set; }

    /// <summary>The address people open, e.g. <c>relays.example.com</c>: the tunnel's public hostname.</summary>
    public string? Address { get; set; }

    /// <summary>The Access team domain, e.g. <c>myteam.cloudflareaccess.com</c>.</summary>
    public string? TeamDomain { get; set; }

    /// <summary>The Access application's AUD tag.</summary>
    public string? Audience { get; set; }
}
