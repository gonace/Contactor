using System.Net;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The tab bar's order, as the server first renders it: Relays and Events, the boards' list, then Debug (while Debugging
/// is on) and Settings in the group a desktop puts at the bottom of the side menu, just above the version. The CSS puts
/// Debug after Settings in the phone's tab bar.
/// </summary>
public sealed class TabBarTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/events")]
    [InlineData("/settings")]
    public async Task Should_PutDebugAndSettings_UnderTheBoardsAndAboveTheVersion(string path)
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John });
        using var http = app.CreateClient();
        // The first page makes John's row, which keeps his setting.
        await http.GetStringAsync("/", Cancel);
        await app.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(John, true, Cancel);

        var nav = Nav(WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel)));

        AssertInOrder(nav, "<a href ", "href=\"events\"", "class=\"side-list\"", "class=\"side-bottom\"", "href=\"debug\"", "href=\"settings\"", "class=\"version\"");
    }

    [Fact]
    public async Task Should_LeaveSettingsAloneAtTheBottom_WhileDebuggingIsOff()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John });
        using var http = app.CreateClient();

        var nav = Nav(WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)));

        Assert.DoesNotContain("href=\"debug\"", nav, StringComparison.Ordinal);
        AssertInOrder(nav, "href=\"events\"", "class=\"side-list\"", "class=\"side-bottom\"", "href=\"settings\"", "class=\"version\"");
    }

    private static string Nav(string html)
    {
        var start = html.IndexOf("<nav class=\"tabs", StringComparison.Ordinal);
        Assert.True(start >= 0, "The page has the tab bar.");
        var end = html.IndexOf("</nav>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static void AssertInOrder(string html, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = html.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, $"\"{part}\" comes after what is before it.");
            at = next;
        }
    }
}
