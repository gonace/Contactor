using Contactor.Boards;
using Contactor.Web.Features.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Health;

/// <summary>The board connection and the running release, as <c>/healthz</c> reports them.</summary>
public sealed record HealthResponse(bool BoardConnected, DateTimeOffset? BoardLastContactUtc, string Version, string? Commit);

public static class HealthEndpoints
{
    public const string Path = "/healthz";

    /// <summary>
    /// <c>/healthz</c>: 200 with the board connection status, for the container health check and the Pi itself. Only
    /// answers requests from this machine (<see cref="ThisMachine"/>); anything else, including a request whose address is
    /// unknown or that came through Tailscale Serve, gets 404.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetHealth).WithTags("Health").ExcludeFromDescription();
        return endpoints;
    }

    private static Results<Ok<HealthResponse>, NotFound> GetHealth(HttpContext context, IRelayBoard board)
    {
        if (!ThisMachine.Asked(context))
        {
            return TypedResults.NotFound();
        }

        var state = board.State;
        var build = AppBuild.Current;
        return TypedResults.Ok(new HealthResponse(state.IsConnected, state.LastContactUtc, build.Version, build.Commit));
    }
}
