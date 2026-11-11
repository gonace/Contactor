using System.Net;

namespace Contactor.Tests.Web;

/// <summary>
/// The relay screen as the server first renders it, with no board connected.
/// </summary>
public sealed class RelaysPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ListActiveRelaysAndGreetTheUser()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Hej Ted", html, StringComparison.Ordinal);
        Assert.Contains("0 av 8 på", html, StringComparison.Ordinal);
        for (var id = 1; id <= 8; id++)
        {
            Assert.Contains($"Relä {id}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Should_WarnAndDisableButtons_WhenTheBoardIsNotConnected()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Ingen kontakt med reläkortet", html, StringComparison.Ordinal);
        Assert.Contains("Lägena nedan kan vara fel.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Slå på Relä 1\"", html, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(html, "disabled"));
    }

    [Fact]
    public async Task Should_ServeTheFonts()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/fonts/ibm-plex-sans-latin-400-normal.woff2", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
