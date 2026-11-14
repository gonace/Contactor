using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Relays;

public static class RelaysEndpoints
{
    /// <summary>Where the Relays screen's board filter is sent.</summary>
    public const string BoardsPath = "/relays/boards";

    // A year: as long as browsers keep a cookie without asking again.
    private static readonly TimeSpan CookieLife = TimeSpan.FromDays(365);

    /// <summary>
    /// <c>POST /relays/boards</c>: the boards the Relays screen shows in this browser (<see cref="BoardFilter"/>), from
    /// the page's own form (a tap on a board), kept in a cookie, then back to the Relays screen, which reads it while
    /// it is made. A form post rather than script, so the page needs no JavaScript and never shows every board first.
    /// Behind the sign-in like every page, and with the form's antiforgery token. Only a view: it switches nothing.
    /// A token that no longer fits (e.g. the person renamed themselves while the page was open: the token covers their
    /// name) changes nothing and goes back to the page, which comes with a fresh token, rather than a bare error.
    /// </summary>
    public static IEndpointRouteBuilder MapRelaysGroup(this IEndpointRouteBuilder endpoints)
    {
        // The token is checked in the handler, so a stale one leads back to the page instead of a 400.
        endpoints.MapPost(BoardsPath, ShowBoardsAsync).WithTags("Relays").ExcludeFromDescription().DisableAntiforgery();
        return endpoints;
    }

    private static async Task<RedirectHttpResult> ShowBoardsAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
        {
            return TypedResults.LocalRedirect("~/");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var filter = BoardFilter.Parse(form["show"].Count == 1 ? form["show"][0] : null);
        if (filter.IsAll)
        {
            context.Response.Cookies.Delete(BoardFilter.CookieName);
        }
        else
        {
            // Lax, as the setup code's cookie, so it stays when coming back from a sign-in page. Secure only over HTTPS:
            // the app itself answers plain HTTP behind cloudflared and on the local network.
            context.Response.Cookies.Append(BoardFilter.CookieName, filter.Format(), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = CookieLife,
            });
        }

        return TypedResults.LocalRedirect("~/");
    }
}
