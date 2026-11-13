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

    /// <summary>
    /// The socket file the app also listens on, for Tailscale Serve only: the image sets
    /// <c>/data/tailscale/contactor.sock</c>. Tailscale identity believes only requests that come in there. Not a TCP
    /// port: tailscaled passes connections from the Tailscale network to any port on 127.0.0.1, past Serve. Empty: no
    /// socket, and Tailscale can't serve the app.
    /// </summary>
    public string AppSocket { get; set; } = "";

    /// <summary>Where Tailscale Serve passes requests to: the app's socket.</summary>
    public string Target => $"unix:{AppSocket}";
}
