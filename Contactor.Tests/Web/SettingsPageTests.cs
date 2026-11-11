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
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("<h1[^>]*>Settings</h1>", html);
        Assert.Matches("id=\"host\"[^>]*value=\"127.0.0.1\"", html);
        Assert.Matches("id=\"port\"[^>]*value=\"1\"", html);
        Assert.Contains("No contact with the relay board at 127.0.0.1:1", html, StringComparison.Ordinal);
        Assert.Contains("Development version", html, StringComparison.Ordinal);
        Assert.Contains("href=\"settings\" aria-current=\"page\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferTheTabOnEveryMainScreen()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        foreach (var path in new[] { "/", "/events" })
        {
            var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
            Assert.Contains("href=\"settings\"", html, StringComparison.Ordinal);
            Assert.Contains("Settings", html, StringComparison.Ordinal);
        }
    }
}
