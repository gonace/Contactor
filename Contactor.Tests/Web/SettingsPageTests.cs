using System.Net;

namespace Contactor.Tests.Web;

public sealed class SettingsPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowTheBoardAddressAndTheVersion()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("<h1[^>]*>Settings</h1>", html);
        Assert.Matches("id=\"host\"[^>]*value=\"127.0.0.1\"", html);
        Assert.Matches("id=\"port\"[^>]*value=\"1\"", html);
        Assert.Matches("id=\"port\"[^>]*placeholder=\"9760\"", html);
        Assert.Contains("No contact with the relay board at 127.0.0.1:1", html, StringComparison.Ordinal);
        Assert.Contains("Development version", html, StringComparison.Ordinal);
        Assert.Contains("href=\"settings\" aria-current=\"page\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowTheBoardModelsLoginHint()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Vm201:Username"] = "admin",
                ["Vm201:Password"] = "secret",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("id=\"username\"[^>]*value=\"admin\"", html);
        Assert.Contains("At most 8 characters: letters a–z, digits and plain symbols.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AskOnlyForThePassword_OnABoardWithoutAUserName()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Board:Type"] = "devantech-eth484",
                ["Devantech:Host"] = "127.0.0.1",
                ["Devantech:Port"] = "1",
                ["Devantech:Password"] = "quince7",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("The board has no password", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"username\"", html, StringComparison.Ordinal);
        Assert.Matches("id=\"password\"[^>]*placeholder=\"unchanged\"", html);
        Assert.Contains("The TCP password from the board's configuration page, not the login of its web page.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The same as on the board's own web page.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("quince7", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferTheTabOnEveryMainScreen()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        foreach (var path in new[] { "/", "/events" })
        {
            var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
            Assert.Contains("href=\"settings\"", html, StringComparison.Ordinal);
            Assert.Contains("Settings", html, StringComparison.Ordinal);
        }
    }
}
