using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Relays;

public static class RelaysEndpoints
{
    /// <summary>Where the Relays screen's board filter is sent.</summary>
    public const string BoardsPath = "/relays/boards";

    /// <summary>Where a page sends the browser to show every board on Relays: "Add a board" once done.</summary>
    public const string ShowAllPath = "/relays/boards/all";

    // A year: as long as browsers keep a cookie without asking again.
    private static readonly TimeSpan CookieLife = TimeSpan.FromDays(365);

    /// <summary>
    /// <c>POST /relays/boards</c>, <c>/events/boards</c> and <c>/debug/boards</c>: the boards the screen shows in this
    /// browser (<see cref="BoardFilter"/>), from the page's own form (a tap on a board), kept in the screen's own cookie
    /// (<see cref="BoardFilterScreen"/>), then back to the screen, which reads it while it is made. A form post rather
    /// than script, so the page needs no JavaScript and never shows every board first. Behind the sign-in like every
    /// page, and with the form's antiforgery token. Only a view: it switches nothing. A token that no longer fits (e.g.
    /// the person renamed themselves while the page was open: the token covers their name) changes nothing and goes
    /// back to the page, which comes with a fresh token, rather than a bare error.
    /// </summary>
    public static IEndpointRouteBuilder MapRelaysGroup(this IEndpointRouteBuilder endpoints)
    {
        foreach (var screen in BoardFilterScreen.All)
        {
            // The token is checked in the handler, so a stale one leads back to the page instead of a 400.
            endpoints.MapPost(screen.FormPath, (HttpContext context, IAntiforgery antiforgery) => ShowBoardsAsync(context, antiforgery, screen))
                .WithTags("Relays").ExcludeFromDescription().DisableAntiforgery();
        }

        endpoints.MapGet(ShowAllPath, ShowAllBoards).WithTags("Relays").ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>
    /// <c>GET /relays/boards/all</c>: Relays shows every board again, then goes to Relays. "Add a board" ends here, so
    /// the board just added shows whatever the filter was. A link rather than a form: it takes no input, and the only
    /// thing it can do is show every board, so it needs no token. Behind the sign-in like every page.
    /// </summary>
    private static RedirectHttpResult ShowAllBoards(HttpContext context)
    {
        context.Response.Cookies.Delete(BoardFilterScreen.Relays.CookieName);
        return TypedResults.LocalRedirect("~" + BoardFilterScreen.Relays.PagePath);
    }

    private static async Task<RedirectHttpResult> ShowBoardsAsync(HttpContext context, IAntiforgery antiforgery, BoardFilterScreen screen)
    {
        var back = "~" + screen.PagePath;
        IFormCollection form;
        try
        {
            if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
            {
                return TypedResults.LocalRedirect(back);
            }

            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or AntiforgeryValidationException)
        {
            // A form over ASP.NET Core's limits (e.g. too many values), which the token check reads first: no filter of
            // ours, so back to the page.
            return TypedResults.LocalRedirect(back);
        }

        var filter = BoardFilter.Parse(form["show"].Count == 1 ? form["show"][0] : null);
        if (filter.IsAll)
        {
            context.Response.Cookies.Delete(screen.CookieName);
        }
        else
        {
            // Lax, as the setup code's cookie, so it stays when coming back from a sign-in page. Secure only over HTTPS:
            // the app itself answers plain HTTP behind cloudflared and on the local network.
            context.Response.Cookies.Append(screen.CookieName, filter.Format(), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = CookieLife,
            });
        }

        return TypedResults.LocalRedirect(back);
    }
}
