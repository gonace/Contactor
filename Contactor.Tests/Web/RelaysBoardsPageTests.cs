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
    public async Task Should_RefuseTheFilter_WithoutTheFormsToken()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();
        await PageAsync(http);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["show"] = "1" });

        using var response = await http.PostAsync(RelaysEndpoints.BoardsPath, form, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"id=\"board-{boards.BarnId}\"", await PageAsync(http), StringComparison.Ordinal);
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

    // The app with its first board and Barn, both in memory.
    private sealed class TwoBoards : IAsyncDisposable
    {
        private TwoBoards(TestWebApp app, FakeRelayBoard first, FakeRelayBoard barn, int barnId)
        {
            App = app;
            First = first;
            Barn = barn;
            BarnId = barnId;
        }

        public TestWebApp App { get; }

        public FakeRelayBoard First { get; }

        public FakeRelayBoard Barn { get; }

        public int BarnId { get; }

        public IDbContextFactory<ContactorDbContext> Factory => App.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();

        public static async Task<TwoBoards> StartAsync(FakeRelayBoard? barn = null)
        {
            var first = new FakeRelayBoard();
            barn ??= new FakeRelayBoard(relayCount: 4, inputCount: 2);
            var app = new TestWebApp(
                "Development",
                new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
                TestWebApp.WithBoard(first));
            var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
            int barnId;
            await using (var db = await factory.CreateDbContextAsync(Cancel))
            {
                var row = new Board { Name = "Barn", SortOrder = 2 };
                db.Boards.Add(row);
                await db.SaveChangesAsync(Cancel);
                barnId = row.Id;
            }

            await app.Services.SeedBoardAsync(barnId, barn.Capabilities.RelayCount, barn.Capabilities.InputCount, Cancel);
            await using (var db = await factory.CreateDbContextAsync(Cancel))
            {
                await db.Relays.Where(r => r.BoardId == barnId && r.Channel == 1).ExecuteUpdateAsync(r => r.SetProperty(x => x.Name, "Barn light"), Cancel);
            }

            await app.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(barn).Build(barnId), Cancel);
            return new TwoBoards(app, first, barn, barnId);
        }

        public ValueTask DisposeAsync()
        {
            return App.DisposeAsync();
        }
    }
}
