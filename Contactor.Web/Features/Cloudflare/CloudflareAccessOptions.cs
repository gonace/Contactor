namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// The <c>CloudflareAccess</c> configuration section: Cloudflare Access's team and application, for an installation
/// set up before the setup guide saved them (<see cref="CloudflareSettings"/>).
/// </summary>
public sealed class CloudflareAccessOptions
{
    public const string SectionName = "CloudflareAccess";

    /// <summary>The Access team domain, e.g. <c>myteam.cloudflareaccess.com</c>: a host name only.</summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>The Access application's AUD tag.</summary>
    public string Audience { get; set; } = "";
}
