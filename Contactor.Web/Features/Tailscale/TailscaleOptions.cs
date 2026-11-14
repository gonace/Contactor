using System.Net;

namespace Contactor.Web.Features.Tailscale;

/// <summary>The <c>Tailscale</c> configuration section: where Tailscale is, and what it serves. The image's are the defaults.</summary>
public sealed class TailscaleOptions
{
    public const string SectionName = "Tailscale";

    // The loopback network's own address and its broadcast address, which Kestrel can't listen on.
    private static readonly IPAddress LoopbackNetwork = IPAddress.Parse("127.0.0.0");
    private static readonly IPAddress LoopbackBroadcast = IPAddress.Parse("127.255.255.255");

    /// <summary>Where tailscaled, Tailscale's own service, is.</summary>
    public string DaemonPath { get; set; } = "/usr/local/bin/tailscaled";

    /// <summary>Where tailscale, the command that talks to tailscaled, is.</summary>
    public string CliPath { get; set; } = "/usr/local/bin/tailscale";

    /// <summary>Where tailscaled keeps its state, so the app stays on the Tailscale network after a restart.</summary>
    public string StateDirectory { get; set; } = "/data/tailscale";

    /// <summary>
    /// The address the app also listens on, for Tailscale Serve only: the image sets <c>127.0.0.2:8081</c>. Tailscale
    /// identity believes only requests that come in there. Never on 127.0.0.1: tailscaled passes connections from the
    /// Tailscale network to any port there, past Serve. Serve can't use a socket file, which needs root. Empty: Tailscale
    /// can't serve the app.
    /// </summary>
    public string ServeAddress { get; set; } = "";

    /// <summary>Where Tailscale Serve passes requests to; only used once <see cref="ServeEndPoint"/> holds.</summary>
    public string Target => $"http://{ServeEndPoint}";

    /// <summary>
    /// <see cref="ServeAddress"/> as an address and port, or null when it isn't one only Serve can reach: an IPv4
    /// loopback address other than 127.0.0.1, with a port. Not 127.0.0.0 or 127.255.255.255 either, which Kestrel can't
    /// listen on.
    /// </summary>
    public IPEndPoint? ServeEndPoint => ParseServeAddress(ServeAddress);

    /// <summary>
    /// Whether <paramref name="connection"/> came in on <see cref="ServeAddress"/>: through Tailscale Serve, whose proxy
    /// connects from 127.0.0.1 like a program on this machine. False when there is no such address.
    /// </summary>
    public bool CameThroughServe(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ServeEndPoint is { } serve
            && connection.LocalPort == serve.Port
            && connection.LocalIpAddress is { } local
            && (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).Equals(serve.Address);
    }

    /// <summary>See <see cref="ServeEndPoint"/>.</summary>
    public static IPEndPoint? ParseServeAddress(string? address) =>
        IPEndPoint.TryParse(address ?? "", out var endPoint)
        && endPoint.Port > 0
        && endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && IPAddress.IsLoopback(endPoint.Address)
        && !endPoint.Address.Equals(IPAddress.Loopback)
        && !endPoint.Address.Equals(LoopbackNetwork)
        && !endPoint.Address.Equals(LoopbackBroadcast)
            ? endPoint
            : null;
}
