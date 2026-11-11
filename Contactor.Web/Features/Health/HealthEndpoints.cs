using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Contactor.Vm201;

namespace Contactor.Web.Features.Health;

/// <summary>The board connection and the running release, as <c>/healthz</c> reports them.</summary>
public sealed record HealthResponse(bool Vm201Connected, DateTimeOffset? Vm201LastContactUtc, string Version, string? Commit);

public static class HealthEndpoints
{
    public const string Path = "/healthz";

    /// <summary>
    /// <c>/healthz</c>: 200 with the board connection status, for the container health check and the Pi itself. Only
    /// answers requests from this machine; anything else, including a request whose address is unknown, gets 404.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetHealth).WithTags("Health").ExcludeFromDescription();
        return endpoints;
    }

    private static Results<Ok<HealthResponse>, NotFound> GetHealth(HttpContext context, Vm201Client client)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return TypedResults.NotFound();
        }

        var state = client.State;
        var build = AppBuild.Current;
        return TypedResults.Ok(new HealthResponse(state.IsConnected, state.LastContactUtc, build.Version, build.Commit));
    }
}
