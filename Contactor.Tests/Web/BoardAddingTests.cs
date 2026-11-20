using System.Net;
using Contactor.Boards;
using Contactor.Boards.Velleman;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>
/// "Add a board" (<see cref="BoardRegistry.AddBoardAsync(BoardModel, string?, CancellationToken)"/> and its steps,
/// <c>/board/new</c>): a new board's row and rows by channel, its safety services, never the settings' address, a name
/// made unique, Cancel leaving nothing behind, and the last board removed and a board added after that.
/// </summary>
public sealed class BoardAddingTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static BoardModel Model(string type) => BoardCatalogue.Find(type)!.Model;

    // A second VM201 at another address, and a VM204: each a row of its own, last in the list, with its relays and
    // inputs by channel, and no address or login until one is typed, not the settings' (Vm201:Host, 127.0.0.1:1 here).
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
        Assert.Equal(
            Enumerable.Range(1, Model("vm204").Capabilities.AnalogInputCount),
            await db.AnalogInputs.Where(i => i.BoardId == third.BoardId).OrderBy(i => i.Channel).Select(i => i.Channel).ToListAsync(Cancel));
        Assert.False(await db.AnalogInputs.AnyAsync(i => i.BoardId == second.BoardId, Cancel));
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

        // The board added now is the only one, so the first: still without the settings' address, which was the removed
        // board's.
        var added = await registry.AddBoardAsync(Model("vm201"), null, Cancel);
        Assert.True(await registry.FinishAddingAsync(added.BoardId, Cancel));
        Assert.Equal("Velleman VM201", added.Name);
        Assert.False(added.Endpoint.HasHost);
        Assert.Same(added, Assert.Single(registry.All));
        Assert.Contains("RELAY 8", WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)), StringComparison.Ordinal);
    }

    // The steps: model and name, then "Where is the board?" (the new board is made now, without the settings' address),
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
        text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 3", text, StringComparison.Ordinal);

        await page.TypeAsync(PageRenderer.With("id", $"relay-{await RelayIdAsync(boards, garage.BoardId, 1)}"), "Door motor");
        await page.ClickAsync(Button("Done"));

        // To Relays, showing every board, so a filter can't hide the board just added.
        var navigation = Assert.IsType<NavigationException>(Assert.Single(Errors(page)));
        Assert.Equal("http://localhost" + RelaysEndpoints.ShowAllPath, navigation.Location);
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal("Door motor", await db.Relays.Where(r => r.BoardId == garage.BoardId && r.Channel == 1).Select(r => r.Name).SingleAsync(Cancel));
        Assert.NotNull(registry.Find(garage.BoardId));
    }

    // A DAEnetIP2 v2 kit always has a community: the step asks for it, with the hint naming the factory one, and offers
    // no "no password" box. A community the board can't take is refused in words.
    [Fact]
    public async Task Should_AskForTheCommunity_WithoutANoPasswordBox_OnADaenetIp2Kit()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");

        await ChooseAsync(page, "Denkovi", "denkovi-daenetip2-v2-8");
        await page.ClickAsync(Button("Next"));

        var text = await page.TextAsync();
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);
        Assert.Contains("On a new board it is private.", text, StringComparison.Ordinal);
        Assert.Contains("check the address and the community", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The board has no password", text, StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "no-login")));
        Assert.True(await page.HasAsync(PageRenderer.With("id", "password")));
        Assert.False(await page.HasAsync(PageRenderer.With("id", "username")));

        await page.TypeAsync(PageRenderer.With("id", "host"), "192.168.1.100");
        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Contains("Type the password, the board's read-write community.", await page.TextAsync(), StringComparison.Ordinal);

        await page.TypeAsync(PageRenderer.With("id", "password"), "abc");
        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Contains("The password, the board's read-write community, must be 4 to 12 characters.", await page.TextAsync(), StringComparison.Ordinal);

        await page.TypeAsync(PageRenderer.With("id", "password"), "private");
        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(new BoardLogin("", "private"), registry.All[^1].Login);
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

        // The board was never added, so neither "Cancel" nor the other model logs a removal.
        Assert.False(await db.EventLog.AnyAsync(e => e.Action == EventActions.BoardRemoved, Cancel));
    }

    // A board left after "Next" (the page closed half way) isn't on Relays, in its side menu or in "New group", and is
    // unfinished, so the next start removes it. "Go on without a connection", also without an address, finishes it: an
    // open Relays page shows it then.
    [Fact]
    public async Task Should_HideTheBoardMade_UntilItIsFinished()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");

        await ChooseAsync(page, "Velleman", "vm204");
        await page.TypeAsync(PageRenderer.With("id", "board-name"), "Garage");
        await page.ClickAsync(Button("Next"));

        // Opened now, so it draws the boards as they are after "Next".
        using var relays = await PageRenderer.StartAsync<Relays>(boards.App.Services, John, "John");
        Assert.Contains("Barn", await relays.TextAsync(), StringComparison.Ordinal);

        var garage = registry.All[^1];
        Assert.Equal("Garage", garage.Name);
        Assert.True(garage.IsUnfinished);
        Assert.False(registry.IsShown(garage.BoardId));
        Assert.DoesNotContain("Garage", await relays.TextAsync(), StringComparison.Ordinal);
        var candidates = await boards.App.Services.GetRequiredService<GroupsHandler>().CandidatesAsync(Cancel);
        Assert.DoesNotContain(candidates, c => c.BoardId == garage.BoardId);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.True(await db.Boards.Where(b => b.Id == garage.BoardId).Select(b => b.IsUnfinished).SingleAsync(Cancel));
        }

        await page.ClickAsync(Button("Go on without a connection"));

        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(garage.IsUnfinished);
        Assert.True(registry.IsShown(garage.BoardId));
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.False(await db.Boards.Where(b => b.Id == garage.BoardId).Select(b => b.IsUnfinished).SingleAsync(Cancel));
        }

        await BoardTestRig.WaitUntilAsync(() => relays.TextAsync().GetAwaiter().GetResult().Contains("Garage", StringComparison.Ordinal));
    }

    // "Done" after the board answered finishes it: the next start keeps it. (No "Go on without a connection" here, which
    // finishes it too.)
    [Fact]
    public async Task Should_FinishTheBoard_OnDone_AfterItAnswered()
    {
        await using var simulator = new Vm201Simulator();
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
        Assert.True(made.IsUnfinished);

        await page.ClickAsync(Button("Done"));

        var navigation = Assert.IsType<NavigationException>(Assert.Single(Errors(page)));
        Assert.Equal("http://localhost" + RelaysEndpoints.ShowAllPath, navigation.Location);
        Assert.False(made.IsUnfinished);
        Assert.True(registry.IsShown(made.BoardId));
        await using var next = new BoardRegistry(
            boards.Factory, boards.App.Services, boards.App.Services.GetRequiredService<IConfiguration>(), TimeProvider.System, NullLoggerFactory.Instance);
        await next.LoadAsync(Cancel);
        Assert.NotNull(next.Find(made.BoardId));
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.Where(b => b.Id == made.BoardId).Select(b => b.IsUnfinished).SingleAsync(Cancel));
    }

    // A VM201 that answers with its own timers on for relays 3 and 5: "This board has timers of its own" comes after
    // "Where is the board?", and "Turn them off" turns off just those timers at the board (one CMD_TMR_DIS for relays 3
    // and 5), then "Name your relays".
    [Fact]
    public async Task Should_AskAboutAVm201sOwnTimers_AndTurnThemOffAtTheBoard()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x14);
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(new FakeRelayBoard()));
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ChooseAsync(page, "Velleman", "vm201");
        await page.ClickAsync(Button("Next"));
        var made = registry.All[^1];
        await page.TypeAsync(PageRenderer.With("id", "host"), "127.0.0.1");
        await page.TypeAsync(PageRenderer.With("id", "port"), simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.ClickAsync(Button("Next"));

        var text = await page.TextAsync();
        Assert.Contains("This board has timers of its own", text, StringComparison.Ordinal);
        Assert.Contains("Relay 3", text, StringComparison.Ordinal);
        Assert.Contains("Relay 5", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 4", text, StringComparison.Ordinal);
        Assert.Equal(0x14, simulator.TimerMask);

        await page.ClickAsync(Button("Turn them off"));

        text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 4 OF 4", text, StringComparison.Ordinal);
        Assert.Equal(0, simulator.TimerMask);
        var sent = Assert.Single(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        Assert.Equal(new byte[] { 0x14 }, sent.Data.ToArray());
        Assert.True(made.IsUnfinished);
        Assert.Empty(Errors(page));
    }

    // A board left after "Next" and set up in Settings instead (its address saved on its page) is finished: the person
    // keeps it, so the next start doesn't remove it.
    [Fact]
    public async Task Should_FinishTheBoard_WhenItsAddressIsSavedInSettings()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        using (var adding = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John"))
        {
            await ChooseAsync(adding, "Velleman", "vm204");
            await adding.ClickAsync(Button("Next"));
        }

        var made = registry.All[^1];
        Assert.True(made.IsUnfinished);
        using var page = await PageRenderer.StartAsync<BoardPage>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = made.BoardId });
        await page.TypeAsync(PageRenderer.With("id", "host"), "192.168.1.80");

        await page.SubmitAsync(PageRenderer.With("class", "card"));

        Assert.Empty(Errors(page));
        Assert.Equal("192.168.1.80", made.Endpoint.Host);
        Assert.False(made.IsUnfinished);
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.Where(b => b.Id == made.BoardId).Select(b => b.IsUnfinished).SingleAsync(Cancel));
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
        await using var simulator = new Vm201Simulator();
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
        Assert.False(await db.EventLog.AnyAsync(e => e.Action == EventActions.BoardRemoved, Cancel));
    }

    // A board added while a page is open brings "All boards" ("All" among the chips) to its side menu and chips, and
    // the board removed again takes it away: on Relays, with the board filter, and on Settings, with the list that goes
    // to Relays (TabBar).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_ShowAndHideAllBoards_AsABoardIsAddedAndRemoved_WhileThePageIsOpen(bool onRelays)
    {
        await using var app = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var page = onRelays
            ? await PageRenderer.StartAsync<Relays>(app.Services, John, "John")
            : await PageRenderer.StartAsync<Settings>(app.Services, John, "John");
        var sideAll = WithClass("side-board all");
        var chipAll = WithClass("chip-filter all");
        Assert.True(await page.HasAsync(WithClass("side-board")));
        Assert.False(await page.HasAsync(sideAll));
        Assert.False(await page.HasAsync(chipAll));

        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var barn = await registry.AddBoardAsync(Model("vm201"), "Barn", Cancel);
        await registry.FinishAddingAsync(barn.BoardId, Cancel);
        await BoardTestRig.WaitUntilAsync(() => page.HasAsync(sideAll).GetAwaiter().GetResult());
        Assert.Contains("All boards", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Contains("Barn", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(onRelays, await page.HasAsync(chipAll));

        Assert.Null(await app.Services.GetRequiredService<BoardRemoval>().RemoveAsync(barn.BoardId, turnOffFirst: false, John, Cancel));
        await BoardTestRig.WaitUntilAsync(() => !page.HasAsync(sideAll).GetAwaiter().GetResult());
        Assert.False(await page.HasAsync(chipAll));
        Assert.DoesNotContain("All boards", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(WithClass("side-board")));
        Assert.Empty(Errors(page));
    }

    // The setup guide's board steps, now shared with "Add a board", still set up the first board as before: its model,
    // the settings' address for it, its relays' names, and "All set".
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

    // An element whose class starts with these classes, e.g. "side-board all".
    private static Func<IReadOnlyDictionary<string, object?>, bool> WithClass(string classes)
    {
        return attributes => attributes.GetValueOrDefault("class")?.ToString()?.StartsWith(classes + " ", StringComparison.Ordinal) == true;
    }

    // What went wrong on the page, but for moving the focus to a new step's heading, which needs a browser.
    private static List<Exception> Errors(PageRenderer page)
    {
        return [.. page.Errors.Where(e => !e.ToString().Contains("ElementReference has not been configured correctly", StringComparison.Ordinal))];
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
