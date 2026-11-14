using System.Net;
using Contactor.Web.Features.Tailscale;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Access;

/// <summary>Requests only the machine the app runs on may make, such as <c>/healthz</c> and <c>/setup/reload</c>.</summary>
public static class ThisMachine
{
    /// <summary>
    /// Whether the request came from this machine: from a loopback address, and not through Tailscale Serve, whose proxy
    /// connects from 127.0.0.1 too but passes on requests from anyone on the Tailscale network.
    /// </summary>
    public static bool Asked(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        return remote is not null
            && IPAddress.IsLoopback(remote)
            && context.RequestServices.GetService<IOptions<TailscaleOptions>>()?.Value.CameThroughServe(context.Connection) != true;
    }
}
