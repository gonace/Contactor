namespace Contactor.Web.Features.Cloudflare;

/// <summary>The <c>Tunnel</c> configuration section: the Cloudflare tunnel the app runs itself, when it has a token.</summary>
public sealed class TunnelOptions
{
    public const string SectionName = "Tunnel";

    /// <summary>
    /// The token of the Cloudflare tunnel made for this installation (<c>Tunnel__Token</c>). Empty: no tunnel, e.g. in
    /// development or behind a tunnel run elsewhere. A secret: never logged, and given to cloudflared only through its
    /// environment, never on its command line.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>Where cloudflared is; the image has it here.</summary>
    public string Path { get; set; } = "/usr/local/bin/cloudflared";
}
