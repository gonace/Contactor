using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Events, Debug and the tab bar's toasts with two boards (<see cref="TwoBoards"/>): each event and step names its
/// board, each screen has the board filter, kept in a cookie of its own, and the toasts name their board. With one board
/// they are as they always were.
/// </summary>
public sealed partial class EventsDebugBoardsPageTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_NameEachEventsBoard_AndTheBoardAsTheSubject()
    {
        await using var boards = await TwoBoards.StartAsync();
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, RelayId = await RelayIdAsync(boards, boards.BarnId, 1), Actor = John, Action = EventActions.On });
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, Actor = EventActors.System, Action = EventActions.BoardTimersDisabled });
        await AddEventAsync(boards, new EventLogEntry { BoardId = 1, Actor = John, Action = EventActions.BoardLoginChanged });
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http, "/events");

        Assert.Matches("<span class=\"where\"[^>]*>Barn</span>\\s*Barn light", html);
        Assert.Matches("<strong[^>]*>Barn</strong> had its own timers turned off", html);
        Assert.DoesNotContain("The board", html, StringComparison.Ordinal);

        // A person's change to a board, with no target: the board alone below it.
        Assert.Matches($"changed the relay board's login</span>\\s*<span class=\"target\"[^>]*>\\s*<span class=\"where\"[^>]*>{Regex.Escape(FirstName(boards))}</span>", html);
        Assert.Contains($"form id=\"board-filter\" method=\"post\" action=\"events/boards\"", html, StringComparison.Ordinal);
        Assert.Contains(">All boards</button>", html, StringComparison.Ordinal);

        // The side menu's counts are the Relays screen's.
        Assert.Contains("0 of 12 on · 2 boards", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowOnlyThePickedBoardsEvents_AndEventsAboutNoBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, RelayId = await RelayIdAsync(boards, boards.BarnId, 1), Actor = John, Action = EventActions.On });
        await AddEventAsync(boards, new EventLogEntry { BoardId = 1, RelayId = await RelayIdAsync(boards, 1, 3), Actor = John, Action = EventActions.Off });
        await AddEventAsync(boards, new EventLogEntry { Actor = EventActors.System, Action = "board-unknown-thing" });
        using var http = boards.App.CreateClient();
        var barn = Id(boards.BarnId);

        var picked = await PickAsync(http, BoardFilterScreen.Events, barn);

        Assert.Contains("Barn light", picked, StringComparison.Ordinal);
        Assert.DoesNotContain("turned off", picked, StringComparison.Ordinal);
        Assert.Contains("board-unknown-thing", picked, StringComparison.Ordinal);
        Assert.Contains("Showing Barn · ", picked, StringComparison.Ordinal);
        Assert.Matches("class=\"chip-filter picked \" aria-pressed=\"true\"[^>]*>\\s*<span class=\"cn\"[^>]*>\\s*Barn\\s*</span>", picked);

        // Its own cookie: the Relays screen still shows every board, and Debug too.
        var relays = await PageAsync(http, "/");
        Assert.Contains("id=\"board-1\"", relays, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing", await PageAsync(http, "/debug"), StringComparison.Ordinal);

        // Both boards are every board again, as is "All boards".
        var both = await PickAsync(http, BoardFilterScreen.Events, $"1-{barn}");
        Assert.Contains("turned off", both, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing", both, StringComparison.Ordinal);
        await PickAsync(http, BoardFilterScreen.Events, "1");
        var all = await PickAsync(http, BoardFilterScreen.Events, BoardFilter.AllValue);
        Assert.Contains("Barn light", all, StringComparison.Ordinal);
        Assert.Contains("turned off", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_FilterByBoard_OnTheServer_WithPeopleOrAutomatic()
    {
        await using var boards = await TwoBoards.StartAsync();
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, RelayId = await RelayIdAsync(boards, boards.BarnId, 1), Actor = John, Action = EventActions.On });
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, RelayId = await RelayIdAsync(boards, boards.BarnId, 2), Actor = EventActors.Schedule, Action = EventActions.On });
        await AddEventAsync(boards, new EventLogEntry { BoardId = 1, RelayId = await RelayIdAsync(boards, 1, 3), Actor = John, Action = EventActions.Off });
        var handler = boards.App.Services.GetRequiredService<EventsHandler>();
        int[] barn = [boards.BarnId];

        var people = await handler.GetAsync(EventFilter.People, null, 50, Cancel, boards: barn);
        var automatic = await handler.GetAsync(EventFilter.Automatic, null, 50, Cancel, boards: barn);
        var first = await handler.GetAsync(EventFilter.All, null, 50, Cancel, boards: [1]);

        Assert.Equal(["Barn light"], people.Select(r => r.View.Target));
        Assert.All(people, r => Assert.Equal("Barn", r.Board));
        Assert.Contains(automatic, r => r.View.Who == "Schedule" && r.Board == "Barn");
        Assert.DoesNotContain(automatic, r => r.View.IsPerson);
        Assert.DoesNotContain(first, r => r.Board == "Barn");
        Assert.Contains(first, r => r.View.What == "turned off");
    }

    [Fact]
    public async Task Should_KeepThePeopleFilter_WithSeveralBoards()
    {
        await using var boards = await TwoBoards.StartAsync();
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, RelayId = await RelayIdAsync(boards, boards.BarnId, 1), Actor = John, Action = EventActions.On });
        await AddEventAsync(boards, new EventLogEntry { BoardId = boards.BarnId, Actor = EventActors.System, Action = EventActions.BoardTimersDisabled });
        using var page = await PageRenderer.StartAsync<Contactor.Web.Components.Events>(boards.App.Services, John, "John");
        Assert.Contains("had its own timers turned off", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.WithText("People"));

        var text = await page.TextAsync();
        Assert.Contains("Barn light", text, StringComparison.Ordinal);
        Assert.DoesNotContain("had its own timers turned off", text, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("debug")]
    public async Task Should_ChangeNothing_WithAStaleToken_OrNone(string screenName)
    {
        var screen = Screen(screenName);
        await using var boards = await TwoBoards.StartAsync();
        using var http = NoRedirects(boards.App);
        var token = TokenPattern().Match(await PageAsync(http, screen.PagePath)).Groups[1].Value;

        using (var none = await PostAsync(http, screen, "1", token: null))
        {
            AssertBackToThePage(none, screen, cookie: null);
        }

        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Users.Where(u => u.Email == John).ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, "Johnny"), Cancel);
        }

        using (var stale = await PostAsync(http, screen, "1", token))
        {
            AssertBackToThePage(stale, screen, cookie: null);
        }

        var fresh = TokenPattern().Match(await PageAsync(http, screen.PagePath)).Groups[1].Value;
        using var valid = await PostAsync(http, screen, "1", fresh);
        AssertBackToThePage(valid, screen, cookie: "1");
    }

    [Theory]
    [InlineData("events")]
    [InlineData("debug")]
    public async Task Should_RefuseTheFilter_WithoutASignIn_OrWhileSettingUp(string screenName)
    {
        var screen = Screen(screenName);
        await using (var app = new TestWebApp("Production", AccessTokens.Settings))
        {
            using var http = NoRedirects(app);
            using var response = await PostAsync(http, screen, "1", "token");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        await using (var app = new TestWebApp(setUp: false))
        {
            using var http = NoRedirects(app);
            using var response = await PostAsync(http, screen, "1", "token");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task Should_MergeEveryBoardsTraffic_ByTime_AndFilterIt()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var first = registry.Find(1)!.Trace;
        var barn = registry.Find(boards.BarnId)!.Trace;
        first.Clear();
        barn.Clear();
        var at = DateTimeOffset.UtcNow;
        first.Record(Step(at, "First step"));
        barn.Record(Step(at.AddSeconds(1), "Barn step"));
        first.Record(Step(at.AddSeconds(2), "Last step"));
        barn.Record(Step(at, "Routine", routine: true));
        using var http = boards.App.CreateClient();

        var html = await PageAsync(http, "/debug");

        var last = html.IndexOf("Last step", StringComparison.Ordinal);
        var middle = html.IndexOf("Barn step", StringComparison.Ordinal);
        var oldest = html.IndexOf("First step", StringComparison.Ordinal);
        Assert.True(last >= 0 && last < middle && middle < oldest, "Newest first, across the boards.");
        Assert.Matches("<span class=\"where\"[^>]*>Barn</span>\\s*Barn step", html);
        Assert.Matches($"<span class=\"where\"[^>]*>{Regex.Escape(FirstName(boards))}</span>\\s*Last step", html);
        Assert.Contains("1 routine step left out", html, StringComparison.Ordinal);
        Assert.Contains("lines per board", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Board: ", html, StringComparison.Ordinal);

        var picked = await PickAsync(http, BoardFilterScreen.Debug, Id(boards.BarnId));
        Assert.Contains("Barn step", picked, StringComparison.Ordinal);
        Assert.DoesNotContain("Last step", picked, StringComparison.Ordinal);
        Assert.Contains("Showing Barn · ", picked, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing", await PageAsync(http, "/events"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowEveryBoardsTraffic_Live_AndLetGoOnClose()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var barn = registry.Find(boards.BarnId)!.Trace;
        var before = Listeners(barn);
        using var page = await PageRenderer.StartAsync<Contactor.Web.Components.Debug>(boards.App.Services, John, "John");
        Assert.True(Listeners(barn) > before);

        barn.Record(Step(DateTimeOffset.UtcNow, "Barn live step"));
        await BoardTestRig.WaitUntilAsync(() => page.TextAsync().Result.Contains("Barn live step", StringComparison.Ordinal));

        // "Clear" clears the boards shown, here every board.
        await page.ClickAsync(PageRenderer.With("class", "clear"));
        Assert.Empty(barn.Items);
        Assert.Empty(registry.Find(1)!.Trace.Items);
        Assert.Empty(page.Errors);

        page.Close();
        Assert.Equal(before, Listeners(barn));
    }

    [Fact]
    public async Task Should_NameTheBoardInATrafficToast_WithSeveralBoards()
    {
        await using var boards = await TwoBoards.StartAsync();
        await EnsureUserAsync(boards.App);
        await boards.App.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(John, true, Cancel);
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var barn = registry.Find(boards.BarnId)!.Trace;
        var before = Listeners(barn);
        using var page = await PageRenderer.StartAsync<Contactor.Web.Components.Events>(boards.App.Services, John, "John");

        barn.Record(Step(DateTimeOffset.UtcNow, "Turn on relay 3"));

        await BoardTestRig.WaitUntilAsync(() => page.HasAsync(PageRenderer.With("class", "toast ")).Result);
        Assert.True(await page.HasAsync(PageRenderer.With("class", "where")));
        Assert.Matches("→\\s*Barn\\s*Turn on relay 3", await page.TextAsync());
        Assert.Empty(page.Errors);

        page.Close();
        Assert.Equal(before, Listeners(barn));
    }

    [Fact]
    public async Task Should_ShowTheBoardsTraffic_AsBefore_WithOneBoard()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(new FakeRelayBoard()));
        await EnsureUserAsync(app);
        await app.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(John, true, Cancel);
        var trace = app.Services.GetRequiredService<BoardRegistry>().All.Single().Trace;
        using var page = await PageRenderer.StartAsync<Contactor.Web.Components.Debug>(app.Services, John, "John");

        trace.Record(Step(DateTimeOffset.UtcNow, "Turn on relay 3"));

        await BoardTestRig.WaitUntilAsync(() => page.HasAsync(PageRenderer.With("class", "toast ")).Result);
        Assert.False(await page.HasAsync(PageRenderer.With("class", "where")));
        Assert.False(await page.HasAsync(PageRenderer.With("id", "board-filter")));
        Assert.Contains("Board: ", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_ShowEvents_AsBefore_WithOneBoard()
    {
        await using var app = new TestWebApp("Development", configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, BoardId = 1, Actor = EventActors.System, Action = EventActions.BoardTimersDisabled });
            db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, BoardId = 1, RelayId = 2, Actor = John, Action = EventActions.On });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = await PageAsync(http, "/events");

        Assert.Matches("<strong[^>]*>The board</strong> had its own timers turned off", html);
        Assert.DoesNotContain("class=\"where\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("board-filter", html, StringComparison.Ordinal);
        Assert.Contains("Relay 2", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ListenToABoardAddedLater_AndLetGoOfEveryTrace()
    {
        var fake = new FakeRelayBoard();
        await using var app = new TestWebApp("Development", configureServices: TestWebApp.WithBoard(fake));
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var heard = new List<(string Board, string Text)>();
        var changes = 0;
        var watch = new BoardTraceWatch(registry, (board, item) => heard.Add((board.Name, item.Text)), () => Interlocked.Increment(ref changes));

        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        var row = new Board { Name = "Barn", SortOrder = 2 };
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            db.Boards.Add(row);
            await db.SaveChangesAsync(Cancel);
        }

        await app.Services.SeedBoardAsync(row.Id, 4, 0, Cancel);
        var barn = new FakeRelayBoard(relayCount: 4, inputCount: 0);
        var live = await registry.ReplaceAsync(new FakeBoardParts(barn).Build(row.Id), Cancel);
        Assert.True(changes > 0, "A board added is a change.");

        live.Trace.Record(Step(DateTimeOffset.UtcNow, "Barn step"));
        Assert.Contains(("Barn", "Barn step"), heard);
        var before = changes;
        live.Trace.Clear();
        Assert.True(changes > before, "A Clear is a change.");

        watch.Dispose();
        Assert.Equal(0, Listeners(live.Trace));
        Assert.Equal(0, Listeners(registry.Find(1)!.Trace));
        live.Trace.Record(Step(DateTimeOffset.UtcNow, "After"));
        Assert.DoesNotContain(heard, h => h.Text == "After");
    }

    [Fact]
    public void Should_SayWhichBoardsShow()
    {
        Assert.Equal("Showing Barn", BoardsText.Showing(["Barn"]));
        Assert.Equal("Showing Workshop and Paint shop", BoardsText.Showing(["Workshop", "Paint shop"]));
        Assert.Equal("Showing Garage, Barn and Office", BoardsText.Showing(["Garage", "Barn", "Office"]));
    }

    [Fact]
    public void Should_NameTheBoard_OnlyForAnEventAboutTheBoard()
    {
        var lost = new EventLogEntry { Actor = EventActors.System, Action = EventActions.ConnectionLost };
        var on = new EventLogEntry { Actor = EventActors.Schedule, Action = EventActions.On };

        Assert.Equal("Garage", EventText.For(lost, null, null, boardName: "Garage").Who);
        Assert.Equal("The board", EventText.For(lost, null, null).Who);
        Assert.Equal("Schedule", EventText.For(on, "Fan", null, boardName: "Garage").Who);
        Assert.True(EventText.IsAboutBoard(lost));
        Assert.False(EventText.IsAboutBoard(on));
    }

    private static BoardTraceEntry Step(DateTimeOffset at, string text, bool routine = false)
    {
        return new BoardTraceEntry(at, BoardTraceDirection.Sent, text, null, false, IsRoutine: routine);
    }

    private static string FirstName(TwoBoards boards)
    {
        return boards.App.Services.GetRequiredService<BoardRegistry>().Find(1)!.Name;
    }

    private static string Id(int id)
    {
        return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static BoardFilterScreen Screen(string name)
    {
        return name == "events" ? BoardFilterScreen.Events : BoardFilterScreen.Debug;
    }

    private static async Task EnsureUserAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        if (!await db.Users.AnyAsync(u => u.Email == John, Cancel))
        {
            db.Users.Add(new User { Email = John, DisplayName = "John" });
            await db.SaveChangesAsync(Cancel);
        }
    }

    private static async Task AddEventAsync(TwoBoards boards, EventLogEntry entry)
    {
        entry.TimestampUtc = DateTime.UtcNow;
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task<int> RelayIdAsync(TwoBoards boards, int boardId, int channel)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }

    private static async Task<string> PageAsync(HttpClient http, string path)
    {
        return WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
    }

    // Taps a board in the screen's filter: the page's form, with its antiforgery token, and back to the page.
    private static async Task<string> PickAsync(HttpClient http, BoardFilterScreen screen, string show)
    {
        var token = TokenPattern().Match(await PageAsync(http, screen.PagePath));
        Assert.True(token.Success, "The filter's form has its antiforgery token.");
        using var response = await PostAsync(http, screen, show, token.Groups[1].Value);
        response.EnsureSuccessStatusCode();
        Assert.Equal(screen.PagePath, response.RequestMessage!.RequestUri!.AbsolutePath);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancel));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, BoardFilterScreen screen, string show, string? token)
    {
        var fields = new Dictionary<string, string> { ["show"] = show };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        using var form = new FormUrlEncodedContent(fields);
        return await http.PostAsync(screen.FormPath, form, Cancel);
    }

    private static HttpClient NoRedirects(TestWebApp app)
    {
        return app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    // Back to the screen, with its own cookie set to this, or not set at all; never another screen's cookie.
    private static void AssertBackToThePage(HttpResponseMessage response, BoardFilterScreen screen, string? cookie)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(screen.PagePath, response.Headers.Location?.OriginalString);
        var set = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        Assert.DoesNotContain(set, v => BoardFilterScreen.All.Any(s => s != screen && v.StartsWith(s.CookieName + "=", StringComparison.Ordinal)));
        var own = set.Where(v => v.StartsWith(screen.CookieName + "=", StringComparison.Ordinal)).ToList();
        if (cookie is null)
        {
            Assert.Empty(own);
        }
        else
        {
            Assert.StartsWith($"{screen.CookieName}={cookie};", Assert.Single(own), StringComparison.Ordinal);
        }
    }

    // How many listen to a board's new trace items.
    private static int Listeners(BoardTrace trace)
    {
        var field = typeof(BoardTrace).GetField(nameof(BoardTrace.Added), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (field.GetValue(trace) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
