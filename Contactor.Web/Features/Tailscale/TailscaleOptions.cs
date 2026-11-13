namespace Contactor.Web.Features.Tailscale;

/// <summary>The <c>Tailscale</c> configuration section: where Tailscale is, and what it serves. The image's are the defaults.</summary>
public sealed class TailscaleOptions
{
    public const string SectionName = "Tailscale";

    /// <summary>Where tailscaled, Tailscale's own service, is.</summary>
    public string DaemonPath { get; set; } = "/usr/local/bin/tailscaled";

    /// <summary>Where tailscale, the command that talks to tailscaled, is.</summary>
    public string CliPath { get; set; } = "/usr/local/bin/tailscale";

    /// <summary>Where tailscaled keeps its state, so the app stays on the Tailscale network after a restart.</summary>
    public string StateDirectory { get; set; } = "/data/tailscale";

    /// <summary>The app as Tailscale Serve reaches it, on the machine itself.</summary>
    public string Target { get; set; } = "http://127.0.0.1:8080";
}
