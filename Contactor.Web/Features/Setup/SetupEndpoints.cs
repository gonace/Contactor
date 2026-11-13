using System.Net;
using Contactor.Web.Features.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Setup;

public static class SetupEndpoints
{
    /// <summary>Where <c>reset-sign-in</c> tells the running app to read its sign-in again.</summary>
    public const string ReloadPath = "/setup/reload";

    /// <summary>
    /// <c>POST /setup/reload</c>: reads the sign-in again after <c>reset-sign-in</c> changed it in the database, and
    /// makes a new setup code if there is none now. Only answers this machine. It changes nothing itself, and the code
    /// only goes to the log: a tunnel pointed at this machine would let the internet reach it.
    /// </summary>
    public static IEndpointRouteBuilder MapSetupGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ReloadPath, ReloadAsync).WithTags("Setup").ExcludeFromDescription().DisableAntiforgery();
        return endpoints;
    }

    private static async Task<Results<NoContent, NotFound>> ReloadAsync(HttpContext context, SignInChoice choice, SetupCode code)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return TypedResults.NotFound();
        }

        await choice.ApplySavedAsync(context.RequestAborted);
        if (choice.Current is null)
        {
            code.Renew();
        }

        return TypedResults.NoContent();
    }
}
