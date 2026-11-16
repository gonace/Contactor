using System.Net;
using Contactor.Web.Components;
using Contactor.Web.Resources;

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
    public async Task Should_AskForTheCommunityAsThePassword_AndNeverShowIt_OnADaenetIp2Kit()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Board:Type"] = "denkovi-daenetip2-v2-16",
                ["DaenetIp2:Host"] = "127.0.0.1",
                ["DaenetIp2:Port"] = "1",
                ["DaenetIp2:Community"] = "quince7",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Denkovi DAEnetIP2 v2 16-relay kit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"username\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"no-login\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The board has no password", html, StringComparison.Ordinal);
        Assert.Matches("id=\"password\"[^>]*placeholder=\"unchanged\"", html);
        Assert.Contains("On a new board it is private.", html, StringComparison.Ordinal);
        Assert.Contains("With a wrong community the board doesn't answer: check the address and the community.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("quince7", html, StringComparison.Ordinal);
    }

    // Before any community is saved the field is still there, with the hint: the board always has one.
    [Fact]
    public async Task Should_AskForTheCommunity_WhenNoneIsSaved_OnADaenetIp2Kit()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Board:Type"] = "denkovi-daenetip2-v2-8",
                ["DaenetIp2:Host"] = "127.0.0.1",
                ["DaenetIp2:Port"] = "1",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.DoesNotContain("id=\"no-login\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("On a new board it is private.", html, StringComparison.Ordinal);
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

    // On a desktop two columns, each its own stack: the workshop's own things on the left, the app itself on the right.
    [Fact]
    public async Task Should_PutTheCardsInTwoColumns_WithOneBoard()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        var (left, right) = Columns(WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel)));

        AssertInOrder(left, "class=\"board-stack boards\"", "id=\"host\"", "Add a board", "class=\"card people\"", "class=\"card backups\"");
        AssertInOrder(right, "class=\"card updates\"", "class=\"card language\"", "id=\"language\"", "class=\"card debugging\"");
    }

    [Fact]
    public async Task Should_PutTheBoardsList_InTheLeftColumn_WithTwoBoards()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();

        var (left, right) = Columns(WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel)));

        AssertInOrder(left, "class=\"card boards\"", "href=\"board/1\"", $"href=\"board/{boards.BarnId}\"", "Add a board", "class=\"card people\"", "class=\"card backups\"");
        AssertInOrder(right, "class=\"card updates\"", "class=\"card language\"", "class=\"card debugging\"");
        Assert.DoesNotContain("board/", right, StringComparison.Ordinal);
    }

    // The page's two columns: what is in the first, and what is in the second, up to the end of the page's content.
    private static (string Left, string Right) Columns(string html)
    {
        var content = html.IndexOf("<main class=\"content\"", StringComparison.Ordinal);
        var first = html.IndexOf("<div class=\"column\"", content, StringComparison.Ordinal);
        var second = html.IndexOf("<div class=\"column\"", first + 1, StringComparison.Ordinal);
        var end = html.IndexOf("</main>", content, StringComparison.Ordinal);
        Assert.True(content >= 0 && first > content && second > first && end > second, "Settings has two columns.");
        return (html[first..second], html[second..end]);
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

    [Theory]
    [InlineData(68 * 1024, "68 kB")]
    [InlineData(100, "1 kB")]
    [InlineData(1363149, "1.3 MB")]
    public void Should_GiveABackupsSize(long bytes, string size)
    {
        Assert.Equal(size, Settings.Size(TestTexts.For<SettingsTexts>(), bytes));
    }

    // Under a language with a decimal comma: its number format, whatever the computer's own.
    [Fact]
    public void Should_GiveABackupsSize_InTheLanguagesNumberFormat()
    {
        using var swedish = TestCulture.Formats("sv-SE");

        Assert.Equal("1,3 MB", Settings.Size(TestTexts.For<SettingsTexts>(), 1363149));
    }

}
