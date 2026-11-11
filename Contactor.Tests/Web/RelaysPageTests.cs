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
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Hi John", html, StringComparison.Ordinal);
        Assert.Contains("0 of 8 on", html, StringComparison.Ordinal);
        for (var id = 1; id <= 8; id++)
        {
            Assert.Contains($"Relay {id}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Should_WarnAndDisableButtons_WhenTheBoardIsNotConnected()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("No contact with the relay board", html, StringComparison.Ordinal);
        Assert.Contains("The states below may be wrong.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Turn on Relay 1\"", html, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(html, "disabled"));
    }

    [Fact]
    public async Task Should_ServeTheFonts()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
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
