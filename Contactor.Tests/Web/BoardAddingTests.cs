using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// "Add a board" (<see cref="BoardRegistry.AddBoardAsync(BoardModel, string?, CancellationToken)"/> and its steps,
/// <c>/board/new</c>): a new board's row and rows by channel, its safety services, never install.sh's address, a name
/// made unique, Cancel leaving nothing behind, and the last board removed and a board added after that.
/// </summary>
public sealed class BoardAddingTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static BoardModel Model(string type) => BoardCatalogue.Find(type)!.Model;

    // A second VM201 at another address, and a VM204: each a row of its own, last in the list, with its relays and
    // inputs by channel, and no address or login until one is typed, not install.sh's (Vm201:Host, 127.0.0.1:1 here).
    [Fact]
    public async Task Should_AddBoards_OfTheSameAndAnotherModel_WithRowsByChannel_AndNeverInstallShsAddress()
    {
        await using var app = new TestWebApp();
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var first = Assert.Single(registry.All);
        Assert.Equal(new BoardEndpoint("127.0.0.1", 1), first.Endpoint);

        var second = await registry.AddBoardAsync(Model("vm201"), null, Cancel);
        var third = await registry.AddBoardAsync(Model("vm204"), "  Garage ", Cancel);
        var fourth = await registry.AddBoardAsync(Model("vm201"), "", Cancel);

        Assert.Equal([first, second, third, fourth], registry.All);
        Assert.Equal(("Velleman VM201 2", "Garage", "Velleman VM201 3"), (second.Name, third.Name, fourth.Name));
        Assert.False(second.Endpoint.HasHost);
        Assert.Equal(new BoardEndpoint("", 9760), second.Endpoint);
        Assert.True(second.Login.IsNone);
        Assert.Equal(new BoardEndpoint("", 80), third.Endpoint);
        Assert.Equal("vm204", third.Model!.Id);
        Assert.Equal(new BoardEndpoint("127.0.0.1", 1), first.Endpoint);

        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        var row = await db.Boards.SingleAsync(b => b.Id == second.BoardId, Cancel);
        Assert.Equal(("vm201", "", 9760, "", ""), (row.Type, row.Host, row.Port, row.Username, row.Password));
        Assert.Equal(
            [1, 2, 3, 4],
            await db.Boards.OrderBy(b => b.SortOrder).Select(b => b.SortOrder).ToListAsync(Cancel));
        Assert.Equal(Enumerable.Range(1, 8), await db.Relays.Where(r => r.BoardId == second.BoardId).OrderBy(r => r.Channel).Select(r => r.Channel).ToListAsync(Cancel));
        Assert.Equal(Enumerable.Range(1, 4), await db.Relays.Where(r => r.BoardId == third.BoardId).OrderBy(r => r.Channel).Select(r => r.Channel).ToListAsync(Cancel));
        Assert.Equal("Relay 1", await db.Relays.Where(r => r.BoardId == third.BoardId && r.Channel == 1).Select(r => r.Name).SingleAsync(Cancel));
        Assert.Equal(Model("vm204").Capabilities.InputCount, await db.Inputs.CountAsync(i => i.BoardId == third.BoardId, Cancel));
        var relays = db.Relays.Where(r => r.BoardId == third.BoardId).Select(r => r.Id);
        Assert.Equal(4, await db.RelayRuntimes.CountAsync(r => relays.Contains(r.RelayId), Cancel));
        Assert.True(await db.BoardRuntimes.AnyAsync(r => r.BoardId == third.BoardId, Cancel));

        // Too long a name is refused before anything is made.
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddBoardAsync(Model("vm201"), new string('x', 41), Cancel));
        Assert.Equal(4, registry.All.Count);
        Assert.Equal(4, await db.Boards.CountAsync(Cancel));
    }

    [Fact]
    public void Should_MakeTheModelsName_Unique_WithinTheLongestName()
    {
        var vm201 = Model("vm201");
        Assert.Equal("Velleman VM201", BoardRegistry.UniqueName(vm201, []));
        Assert.Equal("Velleman VM201", BoardRegistry.UniqueName(vm201, ["Garage"]));
        Assert.Equal("Velleman VM201 2", BoardRegistry.UniqueName(vm201, ["velleman vm201"]));
        Assert.Equal("Velleman VM201 3", BoardRegistry.UniqueName(vm201, ["Velleman VM201", "Velleman VM201 2"]));

        var longer = vm201 with { Brand = new string('B', 40) };
        var name = BoardRegistry.UniqueName(longer, [Board.DefaultName(longer.Name)]);
        Assert.Equal(Board.NameMaxLength, name.Length);
        Assert.EndsWith(" 2", name, StringComparison.Ordinal);
    }

    // A board that can't be made all the way (here its build fails) leaves nothing: no row, no relays, no live board.
    [Fact]
    public async Task Should_LeaveNothing_WhenTheBoardCantBeMade()
    {
        await using var app = new TestWebApp();
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var parts = new FakeBoardParts();

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddBoardAsync(parts.FakeModel, "Shed", _ => throw new InvalidOperationException("No."), Cancel));

        Assert.Single(registry.All);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        Assert.Equal(1, await db.Boards.CountAsync(Cancel));
        Assert.False(await db.Relays.AnyAsync(r => r.BoardId != registry.All[0].BoardId, Cancel));
        Assert.False(await db.Inputs.AnyAsync(r => r.BoardId != registry.All[0].BoardId, Cancel));
    }

    // A board added while the app runs is watched by every safety service before its connection starts: its groups'
    // interlock trips on its own status, and its schedules run on it.
    [Fact]
    public async Task Should_RunTheSafetyServices_OnABoardAddedWhileTheAppRuns()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var sequencer = await rig.StartServiceAsync<GroupSequencer>();
        var live = await rig.AddBoardAsync();
        var shed = rig.Second!;
        Assert.Equal("Shed", live.Name);
        Assert.NotNull(rig.Scheduler.For(rig.SecondBoardId));
        await BoardTestRig.WaitUntilAsync(() => sequencer.RunningLoops == 2);

        var (group, error) = await rig.GroupSetup.CreateAsync(
            new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupMemberDraft(rig.SecondRelayId(1), "Fan", 3, 300), new GroupMemberDraft(rig.SecondRelayId(2), "Heat", 3, 0)]),
            John,
            Cancel);
        Assert.Null(error);
        await rig.Groups.StartGroupAsync(group!.Value, John, Cancel);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 2]), shed.State.Relays);

        // The fan drops at the board: the heat goes off at once, on the new board's own status.
        shed.SetRelay(1, on: false);
        await rig.WaitForEventsAsync(events => events.Any(e => e.Action == EventActions.InterlockTrip && e.BoardId == rig.SecondBoardId));
        await BoardTestRig.WaitUntilAsync(() => !shed.State.IsOn(2));

        // A schedule on the new board's relay turns it on at its time.
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var now = TimeOnly.FromDateTime(rig.Database.Services.GetRequiredService<Contactor.Web.LocalClock>().Now);
            db.Schedules.Add(new Schedule { RelayId = rig.SecondRelayId(3), Days = ScheduleDays.All, OnTime = now, OffTime = now.AddHours(1), IsEnabled = true, CreatedBy = John });
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Scheduler.For(rig.SecondBoardId)!.TickAsync(Cancel);
        Assert.True(shed.State.IsOn(3));
    }

    // The last board can go now; the app then runs none: the pages say so with "Add a board", commands throw, nothing
    // connects, /healthz is healthy, the setup guide isn't opened again, and a board can be added after that.
    [Fact]
    public async Task Should_RemoveTheLastBoard_AndAddOneAfterThat()
    {
        await using var app = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var only = Assert.Single(registry.All);
        using var http = app.CreateClient();
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/board/{only.BoardId}", Cancel));
        Assert.Contains($"Remove {only.Name}", page, StringComparison.Ordinal);

        Assert.Null(await app.Services.GetRequiredService<BoardRemoval>().RemoveAsync(only.BoardId, turnOffFirst: true, John, Cancel));

        Assert.Empty(registry.All);
        var control = app.Services.GetRequiredService<RelayControl>();
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => control.TurnAllOffAsync(only.BoardId, John, Cancel));
        Assert.Empty(await control.TurnOffAllBoardsAsync(John, Cancel));
        var relays = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        Assert.Contains("There's no relay board in Contactor.", relays, StringComparison.Ordinal);
        Assert.Contains("href=\"board/new\" class=\"add-board\"", relays, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"group/new\"", relays, StringComparison.Ordinal);
        Assert.DoesNotContain("Setup ·", relays, StringComparison.Ordinal);
        var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("There's no relay board in Contactor.", settings, StringComparison.Ordinal);
        Assert.Contains("href=\"board/new\" class=\"add-board\"", settings, StringComparison.Ordinal);
        using (var health = await http.GetAsync("/healthz", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Contains("\"boards\":[]", await health.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        }

        // The board added now is the only one, so the first: still without install.sh's address, which was the removed
        // board's.
        var added = await registry.AddBoardAsync(Model("vm201"), null, Cancel);
        Assert.Equal("Velleman VM201", added.Name);
        Assert.False(added.Endpoint.HasHost);
        Assert.Same(added, Assert.Single(registry.All));
        Assert.Contains("RELAY 8", WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)), StringComparison.Ordinal);
    }

    // "+ Add a board": in Settings under the one board's card, at the end of the list of several, at the end of the
    // filter's chips, and in the side menu's list on Relays, Events and Debug.
    [Fact]
    public async Task Should_OfferAddABoard_WhereTheDesignPutsIt()
    {
        await using (var one = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard())))
        {
            using var http = one.CreateClient();
            var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
            Assert.Matches("(?s)class=\"board-stack\".*id=\"host\".*href=\"board/new\" class=\"add-board\"", settings);

            // One board: no filter, so the relays don't offer it; Settings is the place.
            Assert.DoesNotContain("board/new", WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)), StringComparison.Ordinal);
        }

        await using var boards = await TwoBoards.StartAsync();
        using var client = boards.App.CreateClient();
        var list = WebUtility.HtmlDecode(await client.GetStringAsync("/settings", Cancel));
        Assert.Matches($"(?s)href=\"board/{boards.BarnId}\".*href=\"board/new\" class=\"add-board\"", list);
        foreach (var screen in new[] { "/", "/events" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(screen, Cancel));
            Assert.Matches("(?s)class=\"chips-row\".*href=\"board/new\" class=\"chip-add\"", html);
            Assert.Matches("(?s)class=\"side-boards\".*href=\"board/new\" class=\"side-add\"", html);
        }
    }

    // The steps: model and name, then "Where is the board?" (the new board is made now, without install.sh's address),
    // then "Name your relays", and "Done" goes to Relays.
    [Fact]
    public async Task Should_AddABoard_ThroughItsSteps()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        Assert.Contains("STEP 1 OF 3", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.With("href", "settings")));

        await ChooseAsync(page, "Velleman", "vm204");
        await page.TypeAsync(PageRenderer.With("id", "board-name"), "Garage");
        await page.ClickAsync(Button("Next"));

        var garage = registry.All[^1];
        Assert.Equal(("Garage", "vm204", 3), (garage.Name, garage.Model!.Id, registry.All.Count));
        var text = await page.TextAsync();
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);
        Assert.Contains("STEP 2 OF 3", text, StringComparison.Ordinal);
        Assert.True(await page.HasAsync(attributes => attributes.GetValueOrDefault("id")?.ToString() == "host" && attributes.GetValueOrDefault("value")?.ToString() is null or ""));

        await page.TypeAsync(PageRenderer.With("id", "host"), "192.168.1.80");
        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Equal("192.168.1.80", garage.Endpoint.Host);
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);

        await page.TypeAsync(PageRenderer.With("id", $"relay-{await RelayIdAsync(boards, garage.BoardId, 1)}"), "Door motor");
        await page.ClickAsync(Button("Done"));

        var navigation = Assert.IsType<NavigationException>(Assert.Single(Errors(page)));
        Assert.Equal("http://localhost/", navigation.Location);
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal("Door motor", await db.Relays.Where(r => r.BoardId == garage.BoardId && r.Channel == 1).Select(r => r.Name).SingleAsync(Cancel));
        Assert.NotNull(registry.Find(garage.BoardId));
    }

    // Cancel at any step leaves nothing behind: before "Next" nothing was made; after it the board goes again. Back to
    // the first step and another model makes a new board in place of the first.
    [Fact]
    public async Task Should_LeaveNothingBehind_OnCancel()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");

        await ChooseAsync(page, "Velleman", "vm201");
        await page.ClickAsync(Button("Next"));
        var made = registry.All[^1];
        Assert.Equal("Velleman VM201 2", made.Name);

        await page.ClickAsync(PageRenderer.With("class", "back"));
        await ChooseAsync(page, "Velleman", "vm204", brandChosen: true);
        await page.ClickAsync(Button("Next"));
        Assert.Null(registry.Find(made.BoardId));
        var garage = registry.All[^1];
        Assert.Equal("vm204", garage.Model!.Id);
        Assert.Equal(3, registry.All.Count);

        await page.ClickAsync(Button("Cancel"));

        Assert.Equal(2, registry.All.Count);
        Assert.Null(registry.Find(garage.BoardId));
        var navigation = Assert.IsType<NavigationException>(Assert.Single(Errors(page)));
        Assert.Equal("http://localhost/settings", navigation.Location);
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(2, await db.Boards.CountAsync(Cancel));
        Assert.False(await db.Relays.AnyAsync(r => r.BoardId != 1 && r.BoardId != boards.BarnId, Cancel));
        Assert.False(await db.EventLog.AnyAsync(e => e.BoardId != null && e.BoardId != 1 && e.BoardId != boards.BarnId, Cancel));
    }

    // An open Relays page shows a board added while it is open, with its relays: it is told again once the board has
    // its model.
    [Fact]
    public async Task Should_ShowABoardAddedWhileRelaysIsOpen()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<Relays>(boards.App.Services, John, "John");
        var before = Occurrences(await page.TextAsync(), "RELAY 4");
        var parts = new FakeBoardParts(new FakeRelayBoard(relayCount: 4, inputCount: 0));
        var model = parts.FakeModel;
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var told = new List<int>();
        registry.BoardsChanged += (_, _) => told.Add(registry.All[^1].Capabilities.RelayCount);

        await registry.AddBoardAsync(model, "Shed", setup => parts.Build(setup.BoardId, model), Cancel);

        // The last word the screens get is the board with its model, so a page that reloads then sees its relays.
        Assert.Equal(4, told[^1]);

        await BoardTestRig.WaitUntilAsync(() =>
        {
            var text = page.TextAsync().GetAwaiter().GetResult();
            return text.Contains("Shed", StringComparison.Ordinal) && Occurrences(text, "RELAY 4") == before + 1;
        });
    }

    // Back on the first step with the same model, a new name renames the board made, and an empty one gives it the
    // model's name again: no new board either time.
    [Fact]
    public async Task Should_RenameTheBoardMade_WhenBackWithTheSameModel()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Velleman", "vm204");
        await page.TypeAsync(PageRenderer.With("id", "board-name"), "Garage");
        await page.ClickAsync(Button("Next"));
        var made = registry.All[^1];
        Assert.Equal("Garage", made.Name);

        await page.ClickAsync(PageRenderer.With("class", "back"));
        await page.TypeAsync(PageRenderer.With("id", "board-name"), "Garage north");
        await page.ClickAsync(Button("Next"));
        Assert.Equal(("Garage north", 3), (made.Name, registry.All.Count));
        Assert.Same(made, registry.All[^1]);

        await page.ClickAsync(PageRenderer.With("class", "back"));
        await page.TypeAsync(PageRenderer.With("id", "board-name"), "");
        await page.ClickAsync(Button("Next"));
        Assert.Equal(("Velleman VM204", 3), (made.Name, registry.All.Count));
        Assert.Contains("Where is the board?", await page.TextAsync(), StringComparison.Ordinal);
    }

    // Cancel while the board answers and a relay was left on ("Try", or at the board): the relay is turned off first,
    // then the board is removed.
    [Fact]
    public async Task Should_TurnTheRelaysOff_AndRemoveTheBoard_OnCancel_WhileItAnswers()
    {
        await using var simulator = new Contactor.Boards.Vm201.Vm201Simulator();
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Velleman", "vm201");
        await page.ClickAsync(Button("Next"));
        var made = registry.All[^1];
        await page.TypeAsync(PageRenderer.With("id", "host"), "127.0.0.1");
        await page.TypeAsync(PageRenderer.With("id", "port"), simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        await simulator.SwitchRelayAsync(3, turnOn: true);
        await BoardTestRig.WaitUntilAsync(() => made.State.IsOn(3));

        await page.ClickAsync(Button("Cancel"));

        Assert.Equal(0, simulator.Outputs);
        Assert.Null(registry.Find(made.BoardId));
        var navigation = Assert.IsType<NavigationException>(Assert.Single(Errors(page)));
        Assert.Equal("http://localhost/settings", navigation.Location);
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.AnyAsync(b => b.Id == made.BoardId, Cancel));
        Assert.False(await db.Relays.AnyAsync(r => r.BoardId == made.BoardId, Cancel));
    }

    // New group's "Which relays?" with two boards: a section per board under its name; once a relay is picked, the
    // other board's relays wait, with a line saying why; unpicking it frees them again. The order step and the review
    // name the board.
    [Fact]
    public async Task Should_GroupWhichRelays_ByBoard_WithTwoBoards()
    {
        await using var boards = await TwoBoards.StartAsync();
        var firstName = boards.App.Services.GetRequiredService<BoardRegistry>().Find(1)!.Name;
        using var page = await PageRenderer.StartAsync<GroupNew>(boards.App.Services, John, "John");
        await page.TypeAsync(PageRenderer.With("id", "name"), "Fan heater");
        await page.ClickAsync(Button("Next"));

        Assert.True(await page.HasAsync(attributes => attributes.GetValueOrDefault("class")?.ToString() == "board-name" && PageRenderer.WithText(firstName)(attributes)));
        Assert.True(await page.HasAsync(attributes => attributes.GetValueOrDefault("class")?.ToString() == "board-name" && PageRenderer.WithText("Barn")(attributes)));
        Assert.DoesNotContain(GroupsHandler.OneBoardText, await page.TextAsync(), StringComparison.Ordinal);
        var barnLight = Pick(await RelayIdAsync(boards, boards.BarnId, 1));
        var barnRelay2 = Pick(await RelayIdAsync(boards, boards.BarnId, 2));
        var firstRelay = Pick(await RelayIdAsync(boards, 1, 1));
        Assert.False(await DisabledAsync(page, firstRelay));

        await page.ClickAsync(barnLight);
        Assert.Contains(GroupsHandler.OneBoardText, await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await DisabledAsync(page, firstRelay));
        Assert.False(await DisabledAsync(page, barnRelay2));

        await page.ClickAsync(barnLight);
        Assert.DoesNotContain(GroupsHandler.OneBoardText, await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await DisabledAsync(page, firstRelay));

        await page.ClickAsync(barnLight);
        await page.ClickAsync(barnRelay2);
        await page.ClickAsync(Button("Next"));
        Assert.Contains("ON BARN", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(Button("Next"));
        Assert.Contains("GROUP · BARN 1 → 2", await page.TextAsync(), StringComparison.Ordinal);
    }

    // With one board "Which relays?" is as before: one list, no board's name.
    [Fact]
    public async Task Should_KeepWhichRelays_AsItWas_WithOneBoard()
    {
        await using var app = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        using var page = await PageRenderer.StartAsync<GroupNew>(app.Services, John, "John");
        await page.TypeAsync(PageRenderer.With("id", "name"), "Fan heater");
        await page.ClickAsync(Button("Next"));

        Assert.False(await page.HasAsync(PageRenderer.With("class", "board-name")));
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        var relays = await db.Relays.OrderBy(r => r.Channel).Select(r => r.Id).Take(2).ToListAsync(Cancel);
        await page.ClickAsync(Pick(relays[0]));
        await page.ClickAsync(Pick(relays[1]));
        await page.ClickAsync(Button("Next"));
        Assert.DoesNotContain("on-board", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("class", "on-board label")));
        await page.ClickAsync(Button("Next"));
        Assert.Contains("GROUP · RELAY 1 → 2", await page.TextAsync(), StringComparison.Ordinal);
    }

    // With two boards the desktop's side menu lists them on every page: on a page without the filter (Settings, a
    // relay's page) each board posts the Relays screen's own form with that board alone, which goes to Relays showing
    // it; "+ Add a board" ends the list. Relays keeps its filter, and one board has no list.
    [Fact]
    public async Task Should_ListTheBoards_InTheSideMenu_OnEveryPage()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var http = boards.App.CreateClient();
        var relayId = await RelayIdAsync(boards, 1, 1);
        foreach (var path in new[] { "/settings", $"/relay/{relayId}", "/group/new", "/board/new", $"/board/{boards.BarnId}" })
        {
            var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
            Assert.Matches("<form id=\"board-go\" method=\"post\" action=\"relays/boards\"", html);
            Assert.Matches($"(?s)class=\"side-boards\".*form=\"board-go\" name=\"show\" value=\"{boards.BarnId}\".*href=\"board/new\" class=\"side-add\"", html);
            Assert.DoesNotContain("aria-pressed", html[html.IndexOf("side-boards", StringComparison.Ordinal)..], StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"chips-row\"", html, StringComparison.Ordinal);
        }

        var relays = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        Assert.DoesNotContain("board-go", relays, StringComparison.Ordinal);
        Assert.Contains("form=\"board-filter\"", relays, StringComparison.Ordinal);

        await using var one = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var client = one.CreateClient();
        Assert.DoesNotContain("side-boards", WebUtility.HtmlDecode(await client.GetStringAsync("/settings", Cancel)), StringComparison.Ordinal);
    }

    // The side menu's list on a page without the filter shows a board's new name at once.
    [Fact]
    public async Task Should_ShowARenamedBoard_InTheSideMenu()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<RelayDetail>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = await RelayIdAsync(boards, 1, 3) });
        Assert.Contains("Barn", await page.TextAsync(), StringComparison.Ordinal);

        Assert.Null(await boards.App.Services.GetRequiredService<BoardAddressHandler>().RenameAsync(boards.BarnId, "Barn north", Cancel));

        await BoardTestRig.WaitUntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Barn north", StringComparison.Ordinal));
    }

    // The side menu's list redraws only for a change it shows: a status from Barn that changes nothing doesn't redraw
    // a page of the first board's relay (its own board's status, right after, draws it once); a relay switched or
    // contact lost shows in the list.
    [Fact]
    public async Task Should_RedrawTheSideMenu_OnlyForAChangeItShows()
    {
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<RelayDetail>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = await RelayIdAsync(boards, 1, 3) });

        // In order: Barn's status first, then the first board's. A redraw for Barn would make it two. Something else
        // the app does meanwhile (an event logged at start) can draw it too, so a few tries; a redraw for Barn would
        // make every one of them two.
        var once = false;
        for (var attempt = 0; attempt < 5 && !once; attempt++)
        {
            var renders = page.Renders;
            boards.Barn.SetRelay(3, false);
            boards.First.SetRelay(4, false);
            await BoardTestRig.WaitUntilAsync(() => page.Renders > renders);
            once = page.Renders == renders + 1;
        }

        Assert.True(once);
        Assert.False(await page.HasAsync(PageRenderer.With("class", "lamp on")));

        boards.Barn.SetRelay(3, true);
        await BoardTestRig.WaitUntilAsync(() => page.HasAsync(PageRenderer.With("class", "lamp on")).GetAwaiter().GetResult());

        boards.Barn.SetConnected(false);
        await BoardTestRig.WaitUntilAsync(() => page.HasAsync(attributes => attributes.GetValueOrDefault("class")?.ToString()?.Contains("lost", StringComparison.Ordinal) == true).GetAwaiter().GetResult());
        Assert.Empty(Errors(page));
    }

    // The setup guide's board steps, now shared with "Add a board", still set up the first board as before: its model,
    // install.sh's address for it, its relays' names, and "All set".
    [Fact]
    public async Task Should_SetUpTheFirstBoard_ThroughTheGuidesSharedSteps()
    {
        await using var app = new TestWebApp(
            settings: new Dictionary<string, string?> { ["CloudflareAccess:TeamDomain"] = "myteam.cloudflareaccess.com", ["CloudflareAccess:Audience"] = "aud" },
            setUp: false);
        app.SaveBeforeStart(new Installation { AccessType = Contactor.Web.Features.Cloudflare.CloudflareMethods.TunnelId, SignInType = Contactor.Web.Features.Cloudflare.CloudflareMethods.AccessId });
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        Assert.Empty(registry.All);

        // Signed in once, as anyone who opens the guide is.
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);
        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");
        Assert.Contains("About you", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(Button("Next"));
        await ChooseAsync(page, "Velleman", "vm201");
        await page.ClickAsync(Button("Next"));

        var first = Assert.Single(registry.All);
        Assert.Equal("vm201", first.Model!.Id);
        var text = await page.TextAsync();
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);
        Assert.Contains("PART 2 OF 2 · THE BOARD", text, StringComparison.Ordinal);
        Assert.True(await page.HasAsync(attributes => attributes.GetValueOrDefault("id")?.ToString() == "host" && attributes.GetValueOrDefault("value")?.ToString() == "127.0.0.1"));

        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(Button("Next"));

        text = await page.TextAsync();
        Assert.Contains("All set", text, StringComparison.Ordinal);
        Assert.Contains("8 in use", text, StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // What went wrong on the page, but for moving the focus to a new step's heading, which needs a browser.
    private static List<Exception> Errors(PageRenderer page)
    {
        return [.. page.Errors.Where(e => !e.ToString().Contains("ElementReference has not been configured correctly", StringComparison.Ordinal))];
    }

    // A relay to pick on "Which relays?", by its id.
    private static Func<IReadOnlyDictionary<string, object?>, bool> Pick(int relayId)
    {
        return PageRenderer.With("id", $"pick-{relayId}");
    }

    private static Task<bool> DisabledAsync(PageRenderer page, Func<IReadOnlyDictionary<string, object?>, bool> option)
    {
        return page.HasAsync(attributes => option(attributes) && attributes.GetValueOrDefault("disabled") is true);
    }

    // A button by its words: only buttons have a type.
    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
    }

    // "Brand", then "Model", as a person picks them.
    private static async Task ChooseAsync(PageRenderer page, string brand, string model, bool brandChosen = false)
    {
        if (!brandChosen)
        {
            await page.ClickAsync(PageRenderer.With("aria-labelledby", "brand-label brand-value"));
            await page.ClickAsync(PageRenderer.With("value", brand));
        }
        else
        {
            await page.ClickAsync(PageRenderer.With("aria-labelledby", "model-label model-value"));
        }

        await page.ClickAsync(PageRenderer.With("value", model));
    }

    private static async Task<int> RelayIdAsync(TwoBoards boards, int boardId, int channel)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }
}
