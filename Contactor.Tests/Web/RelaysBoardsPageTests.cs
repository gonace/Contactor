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
/// first, and the board filter, kept in a cookie. With one board the screen is as it always was (RelaysPageTests).
/// </summary>
public sealed partial class RelaysBoardsPageTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowASectionPerBoard_InTheBoardsOrder()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http);

        var first = html.IndexOf("<h2 class=\"board-name\" id=\"board-1\"", StringComparison.Ordinal);
        var barn = html.IndexOf($"<h2 class=\"board-name\" id=\"board-{boards.BarnId}\"", StringComparison.Ordinal);
        Assert.True(first >= 0 && barn > first, "The first board's section comes before Barn's.");
        Assert.Matches("Hi John · <strong[^>]*>0 of 12 on</strong>", html);
        Assert.Contains("Test Fake · 0 of 4 on", html, StringComparison.Ordinal);
        Assert.Contains("Barn light", html, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(html, "<h3 class=\"section-title\"[^>]*>Inputs</h3>"));

        // Each board's relays in its own section, RELAY n by its own channel.
        var barnSection = Section(html, boards.BarnId);
        Assert.Contains("Barn light", barnSection, StringComparison.Ordinal);
        Assert.Contains("RELAY 4", barnSection, StringComparison.Ordinal);
        Assert.DoesNotContain("RELAY 5", barnSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Barn light", Section(html, 1), StringComparison.Ordinal);
    }

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
        Assert.Equal("Turn off all 2 relays on 2 boards?", BoardsText.ConfirmAllOff(registry.All.Sum(b => b.State.Relays.Count), registry.All.Count));
        var unreachable = await control.TurnOffAllBoardsAsync(John, Cancel);

        Assert.False(boards.First.State.IsOn(3));
        Assert.True(boards.Barn.State.IsOn(2));
        Assert.Equal(
            "Couldn't reach Barn. Its relays are as they were.",
            BoardsText.Unreachable([.. unreachable.Select(id => registry.Find(id)!.Name)]));
    }

    [Fact]
    public async Task Should_ShowOnlyThePickedBoard_AndRememberItInACookie()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();

        var html = await PickAsync(http, boards.BarnId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.DoesNotContain("id=\"board-1\"", html, StringComparison.Ordinal);
        Assert.Contains($"id=\"board-{boards.BarnId}\"", html, StringComparison.Ordinal);
        Assert.Contains("Showing 1 of 2 boards · 0 of 4 on", html, StringComparison.Ordinal);
        Assert.Matches($"value=\"{BoardFilter.AllValue}\" class=\"chip-filter all \" aria-pressed=\"false\"", html);

        // Kept by the browser: a new page shows the same.
        Assert.Contains($"id=\"board-{boards.BarnId}\"", await PageAsync(http), StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"board-1\"", await PageAsync(http), StringComparison.Ordinal);
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

    [Fact]
    public async Task Should_RefuseTheFilter_WithoutASignIn()
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings);
        using var http = NoRedirects(app);

        using var response = await PostAsync(http, "1", "token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Should_RefuseTheFilter_WhileTheAppIsBeingSetUp()
    {
        await using var app = new TestWebApp(setUp: false);
        using var http = NoRedirects(app);

        using var response = await PostAsync(http, "1", "token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
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
    public async Task Should_RedrawADetailPage_OnlyForItsOwnBoard_AndLetGoOnDispose()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var before = Listeners(registry);
        int inputId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            inputId = await db.Inputs.Where(i => i.BoardId == 1).Select(i => i.Id).FirstAsync(Cancel);
        }

        var pages = new List<PageRenderer>
        {
            await PageRenderer.StartAsync<Contactor.Web.Components.RelayDetail>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = await RelayIdAsync(boards, 1, 3) }),
            await PageRenderer.StartAsync<Contactor.Web.Components.InputDetail>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = inputId }),
            await PageRenderer.StartAsync<Contactor.Web.Components.GroupDetail>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = await AddFirstBoardGroupAsync(boards) }),
        };
        Assert.Equal(before + pages.Count, Listeners(registry));
        var renders = pages.Select(p => p.Renders).ToList();

        // A status from Barn first, then from the pages' own board: each page draws once, for its own board only. A
        // redraw for Barn would come first, as the statuses come in order. Statuses that change nothing, so nothing is
        // logged and nothing else redraws the pages.
        boards.Barn.SetRelay(3, false);
        boards.First.SetRelay(4, false);
        foreach (var (page, i) in pages.Select((p, i) => (p, i)))
        {
            await BoardTestRig.WaitUntilAsync(() => page.Renders > renders[i]);
            Assert.Equal(renders[i] + 1, page.Renders);
            Assert.Empty(page.Errors);
        }

        foreach (var page in pages)
        {
            page.Close();
        }

        Assert.Equal(before, Listeners(registry));
    }

    [Fact]
    public async Task Should_ShowEveryBoardAgain_WhenAllArePickedOrAllIsTapped()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();
        await PickAsync(http, "1");

        // Both picked is every board, so no "Showing".
        var both = await PickAsync(http, $"1-{boards.BarnId}");
        Assert.Contains("id=\"board-1\"", both, StringComparison.Ordinal);
        Assert.Contains($"id=\"board-{boards.BarnId}\"", both, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing", both, StringComparison.Ordinal);

        await PickAsync(http, "1");
        var all = await PickAsync(http, BoardFilter.AllValue);
        Assert.Contains($"id=\"board-{boards.BarnId}\"", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing", all, StringComparison.Ordinal);
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

    [Fact]
    public async Task Should_ShowTheBoardFilter_OnlyWithSeveralBoards()
    {
        await using var app = new TestWebApp("Development", configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var http = app.CreateClient();

        var html = await PageAsync(http);

        Assert.DoesNotContain("board-filter", html, StringComparison.Ordinal);
        Assert.DoesNotContain("board-name", html, StringComparison.Ordinal);
        Assert.Contains("class=\"all-off\"", html, StringComparison.Ordinal);
        Assert.Contains("Board online", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowEachDetailPage_WithItsOwnBoardsState()
    {
        await using var boards = await TwoBoards.StartAsync();

        // A state the group's interlock allows: its Fan (relay 1) on below its Heat (relay 2). Heat on alone would be
        // turned off by the interlock within a second of the group being made, before the group's page is read.
        boards.Barn.SetRelay(1, true);
        boards.Barn.SetRelayAndInput(2, 2, true);
        boards.Barn.SetRelay(3, true);
        using var http = boards.App.CreateClient();
        int inputId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            inputId = await db.Inputs.Where(i => i.BoardId == boards.BarnId && i.Channel == 2).Select(i => i.Id).SingleAsync(Cancel);
        }

        var groupId = await AddBarnGroupAsync(boards);

        var relay = WebUtility.HtmlDecode(await http.GetStringAsync($"/relay/{await RelayIdAsync(boards, boards.BarnId, 3)}", Cancel));
        var group = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));
        var other = WebUtility.HtmlDecode(await http.GetStringAsync($"/relay/{await RelayIdAsync(boards, 1, 3)}", Cancel));
        var input = WebUtility.HtmlDecode(await http.GetStringAsync($"/input/{inputId}", Cancel));

        Assert.Contains(">TURN OFF</button>", relay, StringComparison.Ordinal);
        Assert.Contains(">TURN ON</button>", other, StringComparison.Ordinal);
        Assert.Contains("Closed", input, StringComparison.Ordinal);
        Assert.Contains("TURN OFF THE GROUP", group, StringComparison.Ordinal);

        // Nothing the interlock had to undo.
        await using var check = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await check.EventLog.AnyAsync(e => e.Action == EventActions.InterlockTrip, Cancel));
    }

    [Fact]
    public async Task Should_ListEveryBoardsGroups_NamingTheirBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        await AddBarnGroupAsync(boards);
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http);

        Assert.Contains("GROUP · BARN 1 → 2", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("id=\"groups-heading\"", StringComparison.Ordinal) < html.IndexOf("id=\"board-1\"", StringComparison.Ordinal),
            "The groups come before the boards' sections.");

        // A group counts as one, on its own board.
        Assert.Contains("Test Fake · 0 of 3 on", html, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP · BARN", await PickAsync(http, "1"), StringComparison.Ordinal);
    }

    // Barn's relays 1 and 2 as a group.
    private static async Task<int> AddBarnGroupAsync(TwoBoards boards)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = boards.BarnId, Name = "Barn fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.Members.Add(new RelayGroupMember { RelayId = await RelayIdAsync(boards, boards.BarnId, 1), Position = 1, RoleName = "Fan" });
        group.Members.Add(new RelayGroupMember { RelayId = await RelayIdAsync(boards, boards.BarnId, 2), Position = 2, RoleName = "Heat" });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
        await db.SaveChangesAsync(Cancel);
        return group.Id;
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
    public void Should_ToggleABoard_FromEveryBoardToSeveralAndBack()
    {
        int[] ids = [1, 2, 3];

        var one = BoardFilter.All.Toggle(2, ids);
        Assert.Equal("2", one.Format());
        var two = one.Toggle(3, ids);
        Assert.Equal("2-3", two.Format());
        Assert.True(two.Toggle(1, ids).IsAll);
        Assert.Equal("3", two.Toggle(2, ids).Format());
        Assert.True(one.Toggle(2, ids).IsAll);

        // A board that is gone is left out; with none left, every board shows.
        Assert.Equal("2", BoardFilter.Parse("2-9").For(ids).Format());
        Assert.True(BoardFilter.Parse("9").For(ids).IsAll);
        Assert.True(BoardFilter.Parse("9").For(ids).Shows(1));
    }

    [Fact]
    public void Should_NameTheBoardsItCouldNotReach()
    {
        Assert.Null(BoardsText.Unreachable([]));
        Assert.Equal("Couldn't reach Garage and Barn. Their relays are as they were.", BoardsText.Unreachable(["Garage", "Barn"]));
        Assert.Equal("Couldn't reach Garage, Barn and Office. Their relays are as they were.", BoardsText.Unreachable(["Garage", "Barn", "Office"]));
        Assert.Equal("Turn off every relay on 9 boards?", BoardsText.ConfirmAllOff(0, 9));
        Assert.Equal("Turn off the 1 relay that is on, on 2 boards?", BoardsText.ConfirmAllOff(1, 2));
        Assert.Equal("Turn off all 16 relays on 9 boards?", BoardsText.ConfirmAllOff(16, 9));
    }

    private static async Task<string> PageAsync(HttpClient http)
    {
        return WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
    }

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

    // How many listen to the boards' states.
    private static int Listeners(BoardRegistry registry)
    {
        var field = typeof(BoardRegistry).GetField(nameof(BoardRegistry.StateChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (field.GetValue(registry) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    // The first board's relays 1 and 2 as a group.
    private static async Task<int> AddFirstBoardGroupAsync(TwoBoards boards)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.Members.Add(new RelayGroupMember { RelayId = await RelayIdAsync(boards, 1, 1), Position = 1, RoleName = "Fan" });
        group.Members.Add(new RelayGroupMember { RelayId = await RelayIdAsync(boards, 1, 2), Position = 2, RoleName = "Heat" });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
        await db.SaveChangesAsync(Cancel);
        return group.Id;
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
}
