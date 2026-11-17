using System.Net;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The tab bar's order, as the server first renders it: Relays and Events, the boards' list, then Settings and Debug
/// (while Debugging is on) in the group a desktop puts at the bottom of the side menu, just above the version. The phone's
/// tab bar shows them in the same order: Relays, Events, Settings, Debug.
/// </summary>
public sealed class TabBarTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/events")]
    [InlineData("/settings")]
    public async Task Should_PutSettingsAndDebug_UnderTheBoardsAndAboveTheVersion(string path)
    {
        // Two boards: the side menu lists them (one board has no list).
        await using var boards = await TwoBoards.StartAsync();
        var app = boards.App;
        using var http = app.CreateClient();
        // The first page makes John's row, which keeps his setting.
        await http.GetStringAsync("/", Cancel);
        await app.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(John, true, Cancel);

        var nav = Nav(WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel)));

        AssertInOrder(nav, "<a href ", "href=\"events\"", "class=\"side-list\"", "class=\"side-bottom\"", "href=\"settings\"", "href=\"debug\"", "</div>", "class=\"version\"");
    }

    [Fact]
    public async Task Should_LeaveSettingsAloneAtTheBottom_WhileDebuggingIsOff()
    {
        // Two boards: the side menu lists them (one board has no list).
        await using var boards = await TwoBoards.StartAsync();
        var app = boards.App;
        using var http = app.CreateClient();

        var nav = Nav(WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)));

        Assert.DoesNotContain("href=\"debug\"", nav, StringComparison.Ordinal);
        AssertInOrder(nav, "href=\"events\"", "class=\"side-list\"", "class=\"side-bottom\"", "href=\"settings\"", "</div>", "class=\"version\"");
    }

    // The links hold no box of their own, so the first </div> after the last one closes the bottom group: a link outside
    // the group would come after it.
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
