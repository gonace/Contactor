using Contactor.Web.Features.Access;
using Contactor.Web.Features.Boards;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Health;

/// <summary>The boards' connections and the running release, as <c>/healthz</c> reports them.</summary>
/// <param name="BoardConnected">Whether every board is connected; false while the app has none.</param>
/// <param name="BoardLastContactUtc">The oldest last contact of any board; null while one has had none, or there is none.</param>
/// <param name="Version">The running release.</param>
/// <param name="Commit">Its commit, when known.</param>
/// <param name="Boards">Each board on its own, in the list's order.</param>
public sealed record HealthResponse(
    bool BoardConnected,
    DateTimeOffset? BoardLastContactUtc,
    string Version,
    string? Commit,
    IReadOnlyList<BoardHealth> Boards);

/// <summary>One board's connection, as <c>/healthz</c> reports it.</summary>
/// <param name="Id">The board's row id.</param>
/// <param name="Name">What people call it.</param>
/// <param name="Model">Its model's id in the board catalogue (e.g. <c>vm201</c>); null before one is chosen.</param>
/// <param name="Connected">Whether it is connected.</param>
/// <param name="LastContactUtc">When the app last heard from it; null before it answered.</param>
public sealed record BoardHealth(int Id, string Name, string? Model, bool Connected, DateTimeOffset? LastContactUtc);

public static class HealthEndpoints
{
    public const string Path = "/healthz";

    /// <summary>
    /// <c>/healthz</c>: 200 with the boards' connection status, also while a board has no contact, for the container
    /// health check and the Pi itself. Only answers requests from this machine (<see cref="ThisMachine"/>); anything
    /// else, including a request whose address is unknown or that came through Tailscale Serve, gets 404.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetHealth).WithTags("Health").ExcludeFromDescription();
        return endpoints;
    }

    private static Results<Ok<HealthResponse>, NotFound> GetHealth(HttpContext context, BoardRegistry registry)
    {
        if (!ThisMachine.Asked(context))
        {
            return TypedResults.NotFound();
        }

        var build = AppBuild.Current;
        return TypedResults.Ok(Health(registry.All, build.Version, build.Commit));
    }

    /// <summary>
    /// What <c>/healthz</c> says about these boards. <c>boardConnected</c> and <c>boardLastContactUtc</c> are kept from
    /// when the app ran one board: every board connected, and the oldest contact.
    /// </summary>
    public static HealthResponse Health(IReadOnlyList<LiveBoard> boards, string version, string? commit)
    {
        ArgumentNullException.ThrowIfNull(boards);
        // Each board's state read once, so its two fields agree.
        var each = boards
            .Select(b => (Board: b, State: b.State))
            .Select(b => new BoardHealth(b.Board.BoardId, b.Board.Name, b.Board.Model?.Id, b.State.IsConnected, b.State.LastContactUtc))
            .ToList();
        var connected = each.Count > 0 && each.All(b => b.Connected);
        var oldest = each.Count > 0 && each.All(b => b.LastContactUtc is not null) ? each.Min(b => b.LastContactUtc) : null;
        return new HealthResponse(connected, oldest, version, commit, each);
    }
}
