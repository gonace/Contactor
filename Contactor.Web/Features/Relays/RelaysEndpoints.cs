using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

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
    /// </summary>
    public static IEndpointRouteBuilder MapRelaysGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(BoardsPath, ShowBoards).WithTags("Relays").ExcludeFromDescription();
        return endpoints;
    }

    private static RedirectHttpResult ShowBoards(HttpContext context, [FromForm(Name = "show")] string? show)
    {
        var filter = BoardFilter.Parse(show);
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
