using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Setup;

public static class SetupEndpoints
{
    /// <summary>Where <c>reset-sign-in</c> tells the running app to read its sign-in again.</summary>
    public const string ReloadPath = "/setup/reload";

    /// <summary>
    /// <c>POST /setup/reload</c>: reads the sign-in again after <c>reset-sign-in</c> changed it in the database, and
    /// makes a new setup code when the sign-in has just gone. Only answers this machine (<see cref="ThisMachine"/>). It changes nothing itself, and
    /// the code only goes to the log: a tunnel pointed at this machine would let the internet reach it. Called again,
    /// it makes no new code, so it can't end a cookie or get round the limit on wrong codes.
    /// </summary>
    public static IEndpointRouteBuilder MapSetupGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ReloadPath, ReloadAsync).WithTags("Setup").ExcludeFromDescription().DisableAntiforgery();
        return endpoints;
    }

    private static async Task<Results<NoContent, NotFound>> ReloadAsync(HttpContext context, SignInChoice choice, SetupCode code)
    {
        if (!ThisMachine.Asked(context))
        {
            return TypedResults.NotFound();
        }

        var before = choice.Current;
        await choice.ApplySavedAsync(context.RequestAborted);
        if (before is not null && choice.Current is null)
        {
            code.Renew();
        }

        return TypedResults.NoContent();
    }
}
