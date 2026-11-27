using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The relay screen with two boards, as the server first renders it: the first board (8 relays, 1 input) and Barn
/// (4 relays, 2 inputs), each in memory. A section per board with its own "All off", "Turn off all boards", which asks
/// first, and the board filter, kept in a cookie. With one board the screen is as it always was (RelaysPageTests), with
/// no chips and only the board in the side menu.
/// </summary>
public sealed partial class RelaysBoardsPageTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_GiveEachBoardItsOwnAllOff_AndAskFirstForEveryBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http);

        // "All off" in each board's own section, named after it; one board's big "All off" is gone.
        Assert.Contains("aria-label=\"All off on Barn\"", Section(html, boards.BarnId), StringComparison.Ordinal);
        Assert.DoesNotContain("All off on Barn", Section(html, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"all-off\"", html, StringComparison.Ordinal);

        // "Turn off all boards" only opens its question: nothing asks yet.
        Assert.Contains(">Turn off all boards</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("alertdialog", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_TurnOffOnlyItsBoard_ForABoardsAllOff()
    {
        await using var boards = await TwoBoards.StartAsync();
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(await RelayIdAsync(boards, 1, 3), John, Cancel);
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);

        // What Barn's "All off" does.
        await control.TurnAllOffAsync(boards.BarnId, John, Cancel);

        Assert.True(boards.First.State.IsOn(3));
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
    }

    [Fact]
    public async Task Should_TellWhichBoardsCouldNotBeReached_WhenTurningOffEveryBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(await RelayIdAsync(boards, 1, 3), John, Cancel);
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);
        boards.Barn.SetConnected(false);
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();

        // What the question asks, then what "Turn off" does and says.
        Assert.Equal("Turn off all 2 relays on 2 boards?", TestTexts.Boards.ConfirmAllOff(registry.All.Sum(b => b.State.Relays.Count), registry.All.Count));
        var unreachable = await control.TurnOffAllBoardsAsync(John, Cancel);

        Assert.False(boards.First.State.IsOn(3));
        Assert.True(boards.Barn.State.IsOn(2));
        Assert.Equal(
            "Couldn't reach Barn. Its relays are as they were.",
            TestTexts.Boards.Unreachable([.. unreachable.Select(id => registry.Find(id)!.Name)]));
    }

    [Fact]
    public async Task Should_ChangeNothing_WithoutTheFormsToken()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = NoRedirects(boards.App);
        await PageAsync(http);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["show"] = "1" });

        using var response = await http.PostAsync(RelaysEndpoints.BoardsPath, form, Cancel);

        AssertBackToThePage(response, cookie: null);
        Assert.Contains($"id=\"board-{boards.BarnId}\"", await PageAsync(http), StringComparison.Ordinal);
    }

    // The token covers the person's name: renamed while the page was open, a tap goes back to the page (with a fresh
    // token) and changes nothing, rather than a bare error.
    [Fact]
    public async Task Should_GoBackToThePage_WhenThePersonWasRenamedMeanwhile()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = NoRedirects(boards.App);
        var token = TokenPattern().Match(await PageAsync(http)).Groups[1].Value;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Users.Where(u => u.Email == John).ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, "Johnny"), Cancel);
        }

        using (var stale = await PostAsync(http, "1", token))
        {
            AssertBackToThePage(stale, cookie: null);
        }

        // The page again, with a token for the new name: now it is taken.
        var fresh = TokenPattern().Match(await PageAsync(http)).Groups[1].Value;
        using var valid = await PostAsync(http, "1", fresh);
        AssertBackToThePage(valid, cookie: "1");
    }

    // A form over ASP.NET Core's limits goes back to the page, as a wrong token does, rather than an error.
    [Fact]
    public async Task Should_GoBackToThePage_ForAFormOverTheLimits()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = NoRedirects(boards.App);
        var token = TokenPattern().Match(await PageAsync(http)).Groups[1].Value;
        var fields = Enumerable.Range(0, 2000).Select(i => new KeyValuePair<string, string>($"x{i}", "1")).ToList();
        fields.Add(new("show", "1"));
        fields.Add(new("__RequestVerificationToken", token));
        using var form = new FormUrlEncodedContent(fields);

        using var response = await http.PostAsync(RelaysEndpoints.BoardsPath, form, Cancel);

        AssertBackToThePage(response, cookie: null);
    }

    [Fact]
    public async Task Should_RefuseTheFilter_WithoutASignIn()
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings);
        using var http = NoRedirects(app);

        using var response = await PostAsync(http, "1", "token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var showAll = await http.GetAsync(RelaysEndpoints.ShowAllPath, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, showAll.StatusCode);
        Assert.False(showAll.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Should_RefuseTheFilter_WhileTheAppIsBeingSetUp()
    {
        await using var app = new TestWebApp(setUp: false);
        using var http = NoRedirects(app);

        using var response = await PostAsync(http, "1", "token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));

        // A page while the app is being set up: to the setup code's page.
        using var showAll = await http.GetAsync(RelaysEndpoints.ShowAllPath, Cancel);
        Assert.Equal((HttpStatusCode.Redirect, Contactor.Web.Features.Setup.SetupMode.CodePath), (showAll.StatusCode, showAll.Headers.Location?.OriginalString));
        Assert.False(showAll.Headers.Contains("Set-Cookie"));
    }

    // The page's own buttons, pressed live: a board's "All off" turns off that board only; "Turn off all boards" only
    // asks, and acts once the person says "Turn off".
    [Fact]
    public async Task Should_TurnOffTheBoardsTheButtonsSay_AndAskFirstForEveryBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(await RelayIdAsync(boards, 1, 3), John, Cancel);
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);
        using var page = await PageRenderer.StartAsync<Contactor.Web.Components.Relays>(boards.App.Services, John, "John");

        await page.ClickAsync(PageRenderer.With("aria-label", "All off on Barn"));
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
        Assert.True(boards.First.State.IsOn(3));

        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);
        await page.ClickAsync(PageRenderer.With("class", "all-boards-off"));
        Assert.True(boards.First.State.IsOn(3));
        Assert.True(boards.Barn.State.IsOn(2));
        Assert.True(await page.HasAsync(PageRenderer.With("role", "alertdialog")));

        // "Cancel" changes nothing; "Turn off" turns off every board.
        await page.ClickAsync(PageRenderer.With("class", "cancel"));
        Assert.False(await page.HasAsync(PageRenderer.With("role", "alertdialog")));
        Assert.True(boards.First.State.IsOn(3));
        await page.ClickAsync(PageRenderer.With("class", "all-boards-off"));
        await page.ClickAsync(PageRenderer.With("class", "confirm"));

        Assert.False(boards.First.State.IsOn(3));
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_StillShowABoardWithNoContact_WhenTheFilterHidesIt()
    {
        await using var boards = await TwoBoards.StartAsync(FakeRelayBoard.LostContact(relayCount: 4, inputCount: 2));
        using var http = boards.App.CreateClient();

        var html = await PickAsync(http, "1");

        Assert.DoesNotContain($"id=\"board-{boards.BarnId}\"", html, StringComparison.Ordinal);
        Assert.Matches("class=\"lost-line\"[^>]*>[\\s\\S]*?<strong[^>]*>Barn:</strong> no contact since", html);
        Assert.Matches("class=\"chip-filter  lost\" aria-pressed=\"false\"", html);
    }

    // Issue #423: a board's box in the side menu and its chip show the board's own state: a lamp for each of its relays,
    // lit when on, in use, in a group or not, and "3 of 8 on" counts the same; "All boards" adds them up. The heading and
    // each board's section still count what the page shows, a group as one.
    [Fact]
    public async Task Should_CountEveryRelayOnTheBoard_AsItsLampsShow()
    {
        await using var boards = await TwoBoards.StartAsync();
        string name;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.AddStep("Fan", await RelayIdAsync(boards, 1, 1));
            group.AddStep("Heat", await RelayIdAsync(boards, 1, 2));
            group.AddStep("Extra heat", await RelayIdAsync(boards, 1, 3));
            await db.AddWithRuntimesAsync(group);
            await db.Relays.Where(r => r.BoardId == 1 && r.Channel == 8).ExecuteUpdateAsync(r => r.SetProperty(x => x.IsActive, false), Cancel);
            name = await db.Boards.Where(b => b.Id == 1).Select(b => b.Name).SingleAsync(Cancel);
        }

        // The group's Fan (as its interlock allows), a relay in no group and the inactive relay.
        boards.First.SetRelay(1, true);
        boards.First.SetRelay(5, true);
        boards.First.SetRelay(8, true);
        boards.Barn.SetRelay(2, true);
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http);

        var buttons = Regex.Matches(html, "<button[^>]*class=\"(?<kind>chip-filter|side-board) [^\"]*\"[^>]*>(?:(?!</button>).)*</button>", RegexOptions.Singleline)
            .Where(m => m.Value.Contains(name, StringComparison.Ordinal) && !m.Value.Contains("Barn", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["chip-filter", "side-board"], buttons.Select(m => m.Groups["kind"].Value).Order());
        Assert.All(buttons, button =>
        {
            Assert.Equal(
                ["lamp on", "lamp", "lamp", "lamp", "lamp on", "lamp", "lamp", "lamp on"],
                Regex.Matches(button.Value, "class=\"(lamp(?: on)?) ?\"").Select(m => m.Groups[1].Value));
            Assert.Contains("3 of 8 on", button.Value, StringComparison.Ordinal);
        });
        Assert.Contains(">3/8</span>", buttons.Single(m => m.Groups["kind"].Value == "chip-filter").Value, StringComparison.Ordinal);

        // "All boards" is the two boards' boxes added up: 3 of 8 and 1 of 4.
        var counts = Regex.Matches(html, "<button[^>]*class=\"side-board[^\"]*\"[^>]*>(?:(?!</button>).)*?(\\d+) of (\\d+) on", RegexOptions.Singleline)
            .Select(m => (On: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), All: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal([(4, 12), (3, 8), (1, 4)], counts);
        Assert.Contains("4 of 12 on · 2 boards", html, StringComparison.Ordinal);

        // What the page shows: on the first board the group and relays 4 to 7, with the group and relay 5 on; Barn's 4,
        // with relay 2 on.
        Assert.Matches("Hi John · <strong[^>]*>3 of 9 on</strong>", html);
        Assert.Contains(" · 2 of 5 on", Section(html, 1), StringComparison.Ordinal);
    }

    // Issue #496: a board that has never answered shows its lamps as unknown, never off, and "All boards" leaves it out
    // of its count until its first answer.
    [Fact]
    public async Task Should_LeaveABoardOutOfAllBoards_UntilItsFirstAnswer()
    {
        await using var boards = await TwoBoards.StartAsync(new FakeRelayBoard(relayCount: 4, connected: false, inputCount: 2));
        boards.First.SetRelay(1, true);
        boards.First.SetRelay(5, true);
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http);

        Assert.Equal(["lamp unknown", "lamp unknown", "lamp unknown", "lamp unknown"], BarnLamps(html));
        Assert.Contains("2 of 8 on · 2 boards", html, StringComparison.Ordinal);

        boards.Barn.SetConnected(true);
        boards.Barn.SetRelay(2, true);

        html = await PageAsync(http);

        Assert.Equal(["lamp", "lamp on", "lamp", "lamp"], BarnLamps(html));
        Assert.Contains("3 of 12 on · 2 boards", html, StringComparison.Ordinal);
    }

    // Barn's lamps in its box in the side menu.
    private static IEnumerable<string> BarnLamps(string html)
    {
        var box = Regex.Matches(html, "<button[^>]*class=\"side-board [^\"]*\"[^>]*>(?:(?!</button>).)*</button>", RegexOptions.Singleline)
            .Single(m => m.Value.Contains("Barn", StringComparison.Ordinal));
        return Regex.Matches(box.Value, "class=\"(lamp(?: on| unknown)?) ?\"").Select(m => m.Groups[1].Value);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("all", "")]
    [InlineData("3-1-3", "1-3")]
    [InlineData("1-x", "")]
    [InlineData("-1", "")]
    [InlineData("0", "")]
    [InlineData("1--2", "")]
    [InlineData("99999999999", "")]
    public void Should_ReadTheFilter_AndEveryBoardForAnythingElse(string? value, string expected)
    {
        Assert.Equal(expected, BoardFilter.Parse(value).Format());
    }

    [Fact]
    public void Should_ReadTooLongAFilter_AsEveryBoard()
    {
        Assert.True(BoardFilter.Parse(string.Join('-', Enumerable.Range(1, 65))).IsAll);
        Assert.True(BoardFilter.Parse(new string('1', 401)).IsAll);
    }

    [Fact]
    public void Should_NameTheBoardsItCouldNotReach()
    {
        Assert.Null(TestTexts.Boards.Unreachable([]));
        Assert.Equal("Couldn't reach Garage and Barn. Their relays are as they were.", TestTexts.Boards.Unreachable(["Garage", "Barn"]));
        Assert.Equal("Couldn't reach Garage, Barn and Office. Their relays are as they were.", TestTexts.Boards.Unreachable(["Garage", "Barn", "Office"]));
        Assert.Equal("Turn off every relay on 9 boards?", TestTexts.Boards.ConfirmAllOff(0, 9));
        Assert.Equal("Turn off the 1 relay that is on, on 2 boards?", TestTexts.Boards.ConfirmAllOff(1, 2));
        Assert.Equal("Turn off all 16 relays on 9 boards?", TestTexts.Boards.ConfirmAllOff(16, 9));
    }

    private static async Task<string> PageAsync(HttpClient http)
    {
        return WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
    }

    // A client that shows a redirect rather than following it, with its cookies.
    private static HttpClient NoRedirects(TestWebApp app)
    {
        return app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string show, string token)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["show"] = show,
            ["__RequestVerificationToken"] = token,
        });
        return await http.PostAsync(RelaysEndpoints.BoardsPath, form, Cancel);
    }

    // Back to the Relays screen, with the filter's cookie set to this, or not set at all.
    private static void AssertBackToThePage(HttpResponseMessage response, string? cookie)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var set = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(BoardFilter.CookieName + "=", StringComparison.Ordinal)).ToList()
            : [];
        if (cookie is null)
        {
            Assert.Empty(set);
        }
        else
        {
            Assert.StartsWith($"{BoardFilter.CookieName}={cookie};", Assert.Single(set), StringComparison.Ordinal);
        }
    }

    // One board's section of the page.
    private static string Section(string html, int boardId)
    {
        var start = html.IndexOf($"aria-labelledby=\"board-{boardId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Board {boardId} has a section.");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static async Task<int> RelayIdAsync(TwoBoards boards, int boardId, int channel)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    // Taps a board in the filter: the page's form, with its antiforgery token, and back to the page.
    private static async Task<string> PickAsync(HttpClient http, string show)
    {
        var page = await PageAsync(http);
        var token = TokenPattern().Match(page);
        Assert.True(token.Success, "The filter's form has its antiforgery token.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["show"] = show,
            ["__RequestVerificationToken"] = token.Groups[1].Value,
        });
        using var response = await http.PostAsync(RelaysEndpoints.BoardsPath, form, Cancel);
        response.EnsureSuccessStatusCode();
        Assert.Equal("/", response.RequestMessage!.RequestUri!.AbsolutePath);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancel));
    }
}
