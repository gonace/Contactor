using System.Net;
using System.Text.RegularExpressions;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Web;

/// <summary>
/// The board filter of Events and Debug with two boards (<see cref="TwoBoards"/>): it takes a change only with the
/// form's token, and only from a person signed in to an app that is set up.
/// </summary>
public sealed partial class EventsDebugBoardsPageTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("events")]
    [InlineData("debug")]
    public async Task Should_ChangeNothing_WithAStaleToken_OrNone(string screenName)
    {
        var screen = Screen(screenName);
        await using var boards = await TwoBoards.StartAsync();
        using var http = NoRedirects(boards.App);
        var token = TokenPattern().Match(await PageAsync(http, screen.PagePath)).Groups[1].Value;

        using (var none = await PostAsync(http, screen, "1", token: null))
        {
            AssertBackToThePage(none, screen, cookie: null);
        }

        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Users.Where(u => u.Email == John).ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, "Johnny"), Cancel);
        }

        using (var stale = await PostAsync(http, screen, "1", token))
        {
            AssertBackToThePage(stale, screen, cookie: null);
        }

        var fresh = TokenPattern().Match(await PageAsync(http, screen.PagePath)).Groups[1].Value;
        using var valid = await PostAsync(http, screen, "1", fresh);
        AssertBackToThePage(valid, screen, cookie: "1");
    }

    [Theory]
    [InlineData("events")]
    [InlineData("debug")]
    public async Task Should_RefuseTheFilter_WithoutASignIn_OrWhileSettingUp(string screenName)
    {
        var screen = Screen(screenName);
        await using (var app = new TestWebApp("Production", AccessTokens.Settings))
        {
            using var http = NoRedirects(app);
            using var response = await PostAsync(http, screen, "1", "token");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        await using (var app = new TestWebApp(setUp: false))
        {
            using var http = NoRedirects(app);
            using var response = await PostAsync(http, screen, "1", "token");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    private static BoardFilterScreen Screen(string name)
    {
        return name == "events" ? BoardFilterScreen.Events : BoardFilterScreen.Debug;
    }

    private static async Task<string> PageAsync(HttpClient http, string path)
    {
        return WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, BoardFilterScreen screen, string show, string? token)
    {
        var fields = new Dictionary<string, string> { ["show"] = show };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        using var form = new FormUrlEncodedContent(fields);
        return await http.PostAsync(screen.FormPath, form, Cancel);
    }

    private static HttpClient NoRedirects(TestWebApp app)
    {
        return app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    // Back to the screen, with its own cookie set to this, or not set at all; never another screen's cookie.
    private static void AssertBackToThePage(HttpResponseMessage response, BoardFilterScreen screen, string? cookie)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(screen.PagePath, response.Headers.Location?.OriginalString);
        var set = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        Assert.DoesNotContain(set, v => BoardFilterScreen.All.Any(s => s != screen && v.StartsWith(s.CookieName + "=", StringComparison.Ordinal)));
        var own = set.Where(v => v.StartsWith(screen.CookieName + "=", StringComparison.Ordinal)).ToList();
        if (cookie is null)
        {
            Assert.Empty(own);
        }
        else
        {
            Assert.StartsWith($"{screen.CookieName}={cookie};", Assert.Single(own), StringComparison.Ordinal);
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
