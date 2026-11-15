using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Updates;

public static class UpdateEndpoints
{
    public const string LogPath = "/settings/update-log";

    /// <summary>The address that downloads update <paramref name="id"/>'s log, relative to the app's base.</summary>
    public static string LogLink(string id)
    {
        return $"{LogPath.TrimStart('/')}?id={Uri.EscapeDataString(id)}";
    }

    /// <summary>
    /// <c>/settings/update-log?id=…</c>: an update's log as a <c>.txt</c> file, to send to whoever helps ("Show what
    /// happened" → "Download"). Behind the sign-in like every page. Only a log the helper wrote, named by an update's id,
    /// with secrets stripped (<see cref="UpdateLog"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapUpdatesGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LogPath, GetLogAsync).WithTags("Updates").ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> GetLogAsync(string? id, UpdateFolder folder, CancellationToken cancellationToken)
    {
        if (await UpdateLog.ReadAsync(folder.LogsFolder, id, cancellationToken) is not { } log)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.File(Encoding.UTF8.GetBytes(log), "text/plain; charset=utf-8", $"contactor-update-{id}.txt");
    }
}
