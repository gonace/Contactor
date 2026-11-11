using System.Net;
using System.Text.Json;

namespace Contactor.Tests.Web;

/// <summary>
/// What a phone needs to put the app on its home screen.
/// </summary>
public sealed class PwaTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ServeTheManifest()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/manifest.webmanifest", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Contactor", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("standalone", manifest.RootElement.GetProperty("display").GetString());
        Assert.Equal("portrait", manifest.RootElement.GetProperty("orientation").GetString());
        Assert.Equal("/", manifest.RootElement.GetProperty("id").GetString());
        foreach (var icon in manifest.RootElement.GetProperty("icons").EnumerateArray())
        {
            using var iconResponse = await http.GetAsync("/" + icon.GetProperty("src").GetString(), Cancel);
            Assert.Equal(HttpStatusCode.OK, iconResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Should_LinkTheManifestWithTheAccessCookie()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = await http.GetStringAsync("/", Cancel);

        Assert.Contains("<link rel=\"manifest\" href=\"manifest.webmanifest\" crossorigin=\"use-credentials\"", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"apple-touch-icon\" href=\"icons/apple-touch-icon.png\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NotZoom()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = await http.GetStringAsync("/", Cancel);

        Assert.Contains("maximum-scale=1, user-scalable=no", html, StringComparison.Ordinal);
        var script = System.Text.RegularExpressions.Regex.Match(html, "<script src=\"(phone[^\"]*\\.js)\"").Groups[1].Value;
        Assert.NotEmpty(script);
        var js = await http.GetStringAsync("/" + script, Cancel);
        Assert.Contains("gesturestart", js, StringComparison.Ordinal);
        var css = await http.GetStringAsync("/app.css", Cancel);
        Assert.Contains("touch-action: manipulation", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_FeelLikeAnApp()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = System.Net.WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("viewport-fit=cover", html, StringComparison.Ordinal);
        Assert.Matches("<button[^>]*class=\"all-off\"[^>]*data-vibrate", html);
        Assert.Matches("<button[^>]*data-vibrate[^>]*aria-pressed", html);
        var phone = await http.GetStringAsync("/" + ScriptPath(html, "phone"), Cancel);
        Assert.Contains("navigator.vibrate", phone, StringComparison.Ordinal);
        var css = await http.GetStringAsync("/app.css", Cancel);
        Assert.Contains("-webkit-tap-highlight-color: transparent", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-touch-callout: none", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_RejoinInSwedish()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = System.Net.WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Matches("<div id=\"components-reconnect-modal\"[^>]*>", html);
        Assert.Contains("Ansluter igen…", html, StringComparison.Ordinal);
        Assert.Matches("<button[^>]*id=\"components-reconnect-button\"[^>]*>Försök igen</button>", html);
        var reconnect = await http.GetStringAsync("/" + ScriptPath(html, "reconnect"), Cancel);
        Assert.Contains("location.reload()", reconnect, StringComparison.Ordinal);
        Assert.Contains("Blazor.resumeCircuit()", reconnect, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("_framework/blazor.web.js", StringComparison.Ordinal) < html.IndexOf(ScriptPath(html, "reconnect"), StringComparison.Ordinal),
            "reconnect.js uses Blazor, so it comes after blazor.web.js.");
    }

    [Fact]
    public async Task Should_FollowThePhonesDarkMode()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = await http.GetStringAsync("/", Cancel);
        var css = await http.GetStringAsync("/app.css", Cancel);

        Assert.Contains("<meta name=\"theme-color\" content=\"#16140F\" media=\"(prefers-color-scheme: dark)\"", html, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.Contains("color-scheme: light dark", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_TakeEveryPageColourFromTheTokens()
    {
        // A colour written into a page's CSS stays the same in dark mode; the tokens in app.css change. The only one
        // allowed is white text on the red "Ta bort" button, which is dark enough in both.
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web", "Components");
        var colours = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{3,8}\b");
        List<string> found = [];
        foreach (var file in Directory.GetFiles(folder, "*.razor.css"))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (colours.IsMatch(line) && !line.Contains("color: #FFFFFF;", StringComparison.Ordinal))
                {
                    found.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }

    private static string ScriptPath(string html, string name)
    {
        var path = System.Text.RegularExpressions.Regex.Match(html, $"<script src=\"({name}[^\"]*\\.js)\"").Groups[1].Value;
        Assert.NotEmpty(path);
        return path;
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
    }
}
