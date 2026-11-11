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
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("<h1[^>]*>Inställningar</h1>", html);
        Assert.Matches("id=\"host\"[^>]*value=\"127.0.0.1\"", html);
        Assert.Matches("id=\"port\"[^>]*value=\"1\"", html);
        Assert.Contains("Ingen kontakt med reläkortet på 127.0.0.1:1", html, StringComparison.Ordinal);
        Assert.Contains("Utvecklingsversion", html, StringComparison.Ordinal);
        Assert.Contains("href=\"settings\" aria-current=\"page\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferTheTabOnEveryMainScreen()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
        using var http = app.CreateClient();

        foreach (var path in new[] { "/", "/handelser" })
        {
            var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
            Assert.Contains("href=\"settings\"", html, StringComparison.Ordinal);
            Assert.Contains("Inställningar", html, StringComparison.Ordinal);
        }
    }
}
