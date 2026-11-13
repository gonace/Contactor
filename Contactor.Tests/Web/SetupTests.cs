using System.Net;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>The setup guide a new installation opens on, and choosing the model of board there.</summary>
public sealed class SetupTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> John = new() { ["CloudflareAccess:DevUserEmail"] = "john@example.com" };

    [Fact]
    public async Task Should_OpenOnTheGuide_OnANewInstallation()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Welcome to Contactor", html, StringComparison.Ordinal);
        Assert.Contains("STEP 1 OF 5", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Skip for now", html, StringComparison.Ordinal);
        Assert.Contains("value=\"John\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OpenOnTheRelays_OnceSetUp()
    {
        await using var app = new TestWebApp("Development", John);
        using var http = app.CreateClient();

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var guide = WebUtility.HtmlDecode(await http.GetStringAsync("/setup", Cancel));

        Assert.Contains("Hi John", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to Contactor", guide, StringComparison.Ordinal);
        Assert.Contains("Hi John", guide, StringComparison.Ordinal);
    }

    // Before the guide is done there may be no board at all: every page opens on the guide.
    [Theory]
    [InlineData("/settings")]
    [InlineData("/events")]
    [InlineData("/debug")]
    [InlineData("/relay/1")]
    [InlineData("/group/new")]
    public async Task Should_OpenEveryPageOnTheGuide_UntilItIsDone(string page)
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync(page, Cancel));

        Assert.Contains("Welcome to Contactor", html, StringComparison.Ordinal);
        Assert.Contains("STEP 1 OF 5", html, StringComparison.Ordinal);
    }

    // The guide always starts at the beginning: the board is chosen on step 2 before anything after it.
    [Fact]
    public async Task Should_OpenOnTheFirstStep_WhateverTheAddress()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/setup?step=3", Cancel));

        Assert.Contains("STEP 1 OF 5", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_StartWithNoBoard_OnANewInstallation()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);

        var boards = app.Services.GetRequiredService<BoardSwitch>();
        Assert.Null(boards.Model);
        Assert.Equal(0, app.Services.GetRequiredService<IRelayBoard>().Capabilities.RelayCount);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        Assert.False(await db.Relays.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_Start_WithoutABoardAddress()
    {
        var settings = new Dictionary<string, string?>(John) { ["Vm201:Host"] = "" };
        await using var app = new TestWebApp("Development", settings);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("The relay board has no address yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Start_WithoutAVm208Address()
    {
        var settings = new Dictionary<string, string?>(John) { ["Board:Type"] = "vm208", ["Vm208:Host"] = "" };
        await using var app = new TestWebApp("Development", settings);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Velleman VM208", html, StringComparison.Ordinal);
        Assert.Contains("The relay board has no address yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Start_WithoutAWaveshareAddress()
    {
        var settings = new Dictionary<string, string?>(John) { ["Board:Type"] = "waveshare-8b", ["Waveshare:Host"] = "" };
        await using var app = new TestWebApp("Development", settings);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Waveshare Modbus PoE ETH Relay (B)", html, StringComparison.Ordinal);
        Assert.Contains("The relay board has no address yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NotConnect_UntilTheBoardHasAnAddress()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?> { ["Vm201:DisableBoardTimers"] = "false" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.UseBoardAsync("vm201");
        var connection = database.Services.GetRequiredService<IBoardConnection>();
        var client = database.Services.BoardService<Vm201Client>();
        var service = database.Services.GetServices<IHostedService>().OfType<BoardHostedService>().Single();

        await service.StartAsync(Cancel);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), Cancel);
            Assert.Equal(0, simulator.ConnectionCount);

            connection.SetEndpoint(new BoardEndpoint("127.0.0.1", simulator.Port));

            await WaitUntilAsync(() => client.State.IsConnected);
        }
        finally
        {
            await service.StopAsync(Cancel);
        }
    }

    [Fact]
    public async Task Should_NotLogALostConnection_BeforeTheBoardHasAnAddress()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(Now));
        await using var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(time);
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?> { ["Vm201:DisableBoardTimers"] = "false" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.UseBoardAsync("vm201");
        var recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            time.Now += BoardEventRecorder.ConnectionLostAfter * 2;
            await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
            Assert.Empty(await EventsAsync(database));

            // Nothing listens there: from now on the board doesn't answer, and that counts.
            database.Services.GetRequiredService<IBoardConnection>().SetEndpoint(new BoardEndpoint("127.0.0.1", 1));
            await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
            time.Now += BoardEventRecorder.ConnectionLostAfter;

            await WaitUntilAsync(() => EventsAsync(database).GetAwaiter().GetResult().Count == 1);
            Assert.Equal(EventActions.ConnectionLost, Assert.Single(await EventsAsync(database)).Action);
        }
        finally
        {
            await recorder.StopAsync(Cancel);
        }
    }

    [Fact]
    public void Should_SayTheFirstAddressWasSet_NotChanged()
    {
        var first = new EventLogEntry { Actor = "john@example.com", Action = EventActions.BoardAddressChanged, Details = """{"to":"192.168.1.50:9760"}""" };
        var next = new EventLogEntry { Actor = "john@example.com", Action = EventActions.BoardAddressChanged, Details = """{"from":"192.168.1.50:9760","to":"192.168.1.51:9760"}""" };

        Assert.Equal("set the relay board's address to 192.168.1.50:9760", EventText.For(first, null, "John").What);
        Assert.Equal("changed the relay board's address from 192.168.1.50:9760 to 192.168.1.51:9760", EventText.For(next, null, "John").What);
    }

    [Fact]
    public async Task Should_MarkAnInstallationInUse_AsSetUp_WhenUpgraded()
    {
        await using var used = TestDatabase.Create();
        await using var empty = TestDatabase.Create();
        foreach (var database in new[] { used, empty })
        {
            await using var db = await database.Factory.CreateDbContextAsync(Cancel);
            Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
            await db.GetService<IMigrator>().MigrateAsync("20261111174651_BoardLoginMaxLength", cancellationToken: Cancel);
            if (database == used)
            {
                db.Users.Add(new User { Email = "john@example.com", DisplayName = "John" });
                await db.SaveChangesAsync(Cancel);
            }

            await db.Database.MigrateAsync(Cancel);
        }

        await using (var db = await used.Factory.CreateDbContextAsync(Cancel))
        {
            var installation = await db.Installations.SingleAsync(Cancel);
            Assert.NotNull(installation.SetupDoneUtc);
            Assert.Null(installation.BoardType);
        }

        await using (var db = await empty.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.False(await db.Installations.AnyAsync(Cancel));
        }
    }

    [Fact]
    public async Task Should_RunNoBoard_UntilOneIsChosen()
    {
        await using var database = CreateChoiceDatabase(new() { ["Board:Type"] = "vm204" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        var boards = database.Services.GetRequiredService<BoardSwitch>();

        // A new installation, and one part of the way through the guide: Board:Type doesn't choose for it.
        await database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel);
        Assert.Null(boards.Model);
        await AddInstallationAsync(database, new Installation());
        await database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel);

        Assert.Null(boards.Model);
        Assert.Same(BoardSwitch.NoBoard, boards.Capabilities);
        Assert.False(boards.State.HasAnswered);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => boards.TurnOnAsync(ChannelMask.Of(1), Cancel));
        Assert.Throws<InvalidOperationException>(() => boards.SetEndpoint(new BoardEndpoint("127.0.0.1", 9760)));
        Assert.Equal("No relay board has been chosen yet.", boards.CheckLogin(BoardLogin.None));
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Relays.AnyAsync(Cancel));
        Assert.False(await db.Inputs.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_RunTheSavedModel_AndSeedItsRows_AtStart()
    {
        await using var database = CreateChoiceDatabase(new() { ["Board:Type"] = "vm201" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await AddInstallationAsync(database, new Installation { BoardType = "waveshare-16", SetupDoneUtc = Now });

        await database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel);

        Assert.Equal("waveshare-16", database.Services.GetRequiredService<BoardSwitch>().Model?.Id);
        Assert.Equal(16, database.Services.GetRequiredService<IRelayBoard>().Capabilities.RelayCount);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(16, await db.Relays.CountAsync(Cancel));
        Assert.Equal(16, await db.RelayRuntimes.CountAsync(Cancel));
    }

    // An installation in use from before the guide chose the model keeps the board it ran: Board:Type's, or the VM201.
    [Theory]
    [InlineData(null, "vm201")]
    [InlineData("vm208", "vm208")]
    public async Task Should_KeepTheEarlierBoard_OfAnInstallationInUse(string? type, string expected)
    {
        await using var database = CreateChoiceDatabase(new() { ["Board:Type"] = type });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await AddInstallationAsync(database, new Installation { SetupDoneUtc = Now });

        await database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel);

        Assert.Equal(expected, database.Services.GetRequiredService<BoardSwitch>().Model?.Id);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(expected, (await db.Installations.SingleAsync(Cancel)).BoardType);
    }

    [Fact]
    public async Task Should_RefuseToStart_OnASavedModelItDoesNotKnow()
    {
        await using var database = CreateChoiceDatabase();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await AddInstallationAsync(database, new Installation { BoardType = "vm999" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel));
        Assert.Contains("vm999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_RefuseToStart_OnABoardTypeItDoesNotKnow_ForAnInstallationInUse()
    {
        await using var database = CreateChoiceDatabase(new() { ["Board:Type"] = "vm209" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await AddInstallationAsync(database, new Installation { SetupDoneUtc = Now });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(Cancel));
        Assert.Equal("Board:Type is \"vm209\", which isn't a board the app knows. Use one of: vm201, vm204, vm208, waveshare-8, waveshare-8b, waveshare-8c, waveshare-16, waveshare-30.", error.Message);
    }

    [Fact]
    public async Task Should_SwitchToTheChosenModel_WhileTheAppRuns()
    {
        await using var rig = await Rig.StartAsync();
        var boards = rig.Database.Services.GetRequiredService<BoardSwitch>();

        await rig.Setup.ChooseBoardAsync("vm201", Cancel);
        var vm201 = boards.Current;
        await rig.Setup.ChooseBoardAsync(" VM204 ", Cancel);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Setup.ChooseBoardAsync("vm999", Cancel));

        Assert.Equal("vm204", boards.Model?.Id);
        Assert.NotSame(vm201, boards.Current);
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal("vm204", (await db.Installations.SingleAsync(Cancel)).BoardType);
    }

    [Fact]
    public async Task Should_AddTheRowsOfABiggerBoard_WhenChosen()
    {
        await using var rig = await Rig.StartAsync();

        await rig.Setup.ChooseBoardAsync("waveshare-30", Cancel);

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(Enumerable.Range(1, 30), await db.Relays.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Cancel));
        Assert.Equal(30, await db.RelayRuntimes.CountAsync(Cancel));
    }

    [Fact]
    public async Task Should_NotChangeTheModel_OnceDone()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Setup.MarkDoneAsync(Cancel);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Setup.ChooseBoardAsync("vm204", Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await db.Installations.SingleAsync(Cancel)).BoardType);
    }

    [Fact]
    public async Task Should_GiveATriedRelayAnAutoOff_AtTheEndOfTheTry()
    {
        await using var rig = await Rig.StartAsync();

        await rig.Setup.TryAsync(3, "john@example.com", Cancel);

        // The app could stop before the turn-off in memory runs: auto-off ends it then.
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            Assert.Equal(Now + SetupHandler.TryAutoOffAfter, runtime.AutoOffAtUtc);
        }

        await WaitUntilAsync(() => !rig.Board.State.IsOn(3));
    }

    [Fact]
    public async Task Should_BeDone_OnceMarked()
    {
        await using var rig = await Rig.StartAsync();

        Assert.False(await rig.Setup.IsDoneAsync(Cancel));
        await rig.Setup.ChooseBoardAsync("vm201", Cancel);
        Assert.False(await rig.Setup.IsDoneAsync(Cancel));
        await rig.Setup.MarkDoneAsync(Cancel);

        Assert.True(await rig.Setup.IsDoneAsync(Cancel));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var installation = await db.Installations.SingleAsync(Cancel);
        Assert.Equal(Now, installation.SetupDoneUtc);
        Assert.Equal("vm201", installation.BoardType);
    }

    [Fact]
    public async Task Should_SaveNamesAndInUse_KeepingTheDefaultForAnEmptyName()
    {
        await using var rig = await Rig.StartAsync();
        var relays = await rig.Setup.RelaysAsync(Cancel);
        Assert.Equal(8, relays.Count);
        Assert.All(relays, r => Assert.True(r.HasDefaultName));

        var error = await rig.Setup.SaveRelaysAsync(
            [new(1, " Fan – Lathe ", true), new(2, "", true), new(7, "  ", false)],
            "john@example.com",
            Cancel);

        Assert.Null(error);
        relays = await rig.Setup.RelaysAsync(Cancel);
        Assert.Equal(("Fan – Lathe", true), (relays[0].Name, relays[0].IsActive));
        Assert.Equal(("Relay 2", true), (relays[1].Name, relays[1].IsActive));
        Assert.Equal(("Relay 7", false), (relays[6].Name, relays[6].IsActive));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var events = await db.EventLog.OrderBy(e => e.Id).Select(e => new { e.RelayId, e.Action }).ToListAsync(Cancel);
        Assert.Equal([new { RelayId = (int?)1, Action = EventActions.Rename }, new { RelayId = (int?)7, Action = EventActions.Deactivate }], events);
    }

    [Fact]
    public async Task Should_TurnARelayOnForAMoment_WhenTried()
    {
        await using var rig = await Rig.StartAsync();

        await rig.Setup.TryAsync(3, "john@example.com", Cancel);

        Assert.True(rig.Board.State.IsOn(3));
        await WaitUntilAsync(() => !rig.Board.State.IsOn(3));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        await WaitUntilAsync(() => db.EventLog.Count() == 2);
        var actions = await db.EventLog.OrderBy(e => e.Id).Select(e => e.Action).ToListAsync(Cancel);
        Assert.Equal([EventActions.On, EventActions.Off], actions);
    }

    [Fact]
    public async Task Should_StillTurnATriedRelayOff_WhenItsAutoOffCannotBeSaved()
    {
        await using var rig = await Rig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            // Refuses an update that only sets the auto-off: the one "Try" makes after ON has gone out.
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_auto_off BEFORE UPDATE OF \"AutoOffAtUtc\" ON \"RelayRuntime\" " +
                "WHEN NEW.\"OnSinceUtc\" IS OLD.\"OnSinceUtc\" AND NEW.\"AutoOffAtUtc\" IS NOT NULL " +
                "BEGIN SELECT RAISE(ABORT, 'disk full'); END;",
                Cancel);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Setup.TryAsync(3, "john@example.com", Cancel));

        Assert.True(rig.Board.State.IsOn(3));
        await WaitUntilAsync(() => !rig.Board.State.IsOn(3));

        // Until the turn-off has saved its event too, so the test's database isn't deleted under it.
        await WaitUntilAsync(() =>
        {
            using var db = rig.Database.Factory.CreateDbContext();
            return db.EventLog.Any(e => e.RelayId == 3 && e.Action == EventActions.Off);
        });
    }

    [Fact]
    public async Task Should_RefuseToTryARelayInAGroup()
    {
        await using var rig = await Rig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { Name = "Fan heater" };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan", StartDelaySec = 3 });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat", StartDelaySec = 3 });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
        }

        await Assert.ThrowsAsync<RelayInGroupException>(() => rig.Setup.TryAsync(2, "john@example.com", Cancel));

        Assert.False(rig.Board.State.IsOn(2));
        Assert.Equal("Fan heater", (await rig.Setup.RelaysAsync(Cancel))[1].GroupName);
    }

    // The board switched the relay on although "Try"'s ON was reported as failed: it is turned off after the try, not
    // left on for its full auto-off.
    [Fact]
    public async Task Should_TurnATriedRelayOff_WhenItsOnFailedButCameOn()
    {
        await using var rig = await Rig.StartAsync(startRecorder: true);
        rig.Board.SwitchSilentlyThenFail = true;

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Setup.TryAsync(3, JohnEmail, Cancel));
        await rig.Board.RefreshAsync(Cancel);

        await WaitUntilAsync(() => !rig.Board.State.IsOn(3) && OffLogged(rig, JohnEmail));
    }

    private const string JohnEmail = "john@example.com";

    private static bool OffLogged(Rig rig, string actor)
    {
        using var db = rig.Database.Factory.CreateDbContext();
        return db.EventLog.Any(e => e.RelayId == 3 && e.Action == EventActions.Off && e.Actor == actor);
    }

    // An ON that never reached the board leaves nothing to turn off: no OFF goes out and nothing is logged.
    [Fact]
    public async Task Should_SendNoOff_WhenATriedOnFailedAndNothingCameOn()
    {
        var logs = new LogCollector();
        await using var rig = await Rig.StartAsync(logs, startRecorder: true);
        rig.Board.SetConnected(false);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Setup.TryAsync(3, JohnEmail, Cancel));
        rig.Board.SetConnected(true);

        await WaitUntilAsync(() => logs.Contains("\"Try\" of relay 3 failed and the relay didn't come on"));
        Assert.False(rig.Board.State.IsOn(3));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.DoesNotContain(await db.EventLog.ToListAsync(Cancel), e => e.RelayId == 3);
    }

    [Fact]
    public async Task Should_NotTry_WithoutTheBoard()
    {
        await using var rig = await Rig.StartAsync();
        rig.Board.SetConnected(false);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Setup.TryAsync(3, "john@example.com", Cancel));
    }

    [Fact]
    public async Task Should_NotCountASaveOfTheSameAddressAndLogin_AsAMove()
    {
        await using var database = TestDatabase.Create(
            (services, configuration) => services.AddBoards(configuration),
            new Dictionary<string, string?> { ["Vm201:Host"] = "127.0.0.1" });
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.UseBoardAsync("vm201");
        var connection = database.Services.GetRequiredService<IBoardConnection>();
        var (endpoint, login) = (connection.Endpoint, connection.Login);

        connection.SetEndpoint(new BoardEndpoint("127.0.0.1", 9760));
        connection.SetLogin(BoardLogin.None);
        Assert.False(BoardContact.HasMoved(connection, endpoint, login));

        connection.SetEndpoint(new BoardEndpoint("127.0.0.2", 9760));
        Assert.True(BoardContact.HasMoved(connection, endpoint, login));
    }

    [Fact]
    public async Task Should_AskTheBoard_OnCheckAgain()
    {
        var board = new FakeRelayBoard();

        Assert.True(await BoardContact.AskAsync(board, WaitTimeout, TimeProvider.System, Cancel));
    }

    [Fact]
    public async Task Should_SayNotFound_WhenTheBoardDoesNotAnswerTheCheck()
    {
        var silent = new FakeRelayBoard { IgnoreRefresh = true };
        var gone = new FakeRelayBoard(connected: false);

        Assert.False(await BoardContact.AskAsync(silent, TimeSpan.FromMilliseconds(300), TimeProvider.System, Cancel));
        Assert.False(await BoardContact.AskAsync(gone, WaitTimeout, TimeProvider.System, Cancel));
    }

    [Fact]
    public async Task Should_FindTheBoard_WhenAlreadyConnected()
    {
        var board = new FakeRelayBoard();
        using var contact = BoardContact.Watch(board);

        Assert.True(await contact.WaitAsync(moved: false, WaitTimeout, TimeProvider.System, Cancel));
    }

    [Fact]
    public async Task Should_NotCountTheOldConnection_AfterAMove()
    {
        var board = new FakeRelayBoard();
        using var contact = BoardContact.Watch(board);
        var wait = contact.WaitAsync(moved: true, WaitTimeout, TimeProvider.System, Cancel);
        board.SetConnected(true);
        await Task.Delay(50, Cancel);
        Assert.False(wait.IsCompleted);

        board.SetConnected(false);
        board.SetConnected(true);

        Assert.True(await wait);
    }

    // The client reconnects at once: the board can be back at its new address before the save even returns.
    [Fact]
    public async Task Should_FindTheBoard_WhenItReconnectedBeforeTheWait()
    {
        var board = new FakeRelayBoard();
        using var contact = BoardContact.Watch(board);
        board.SetConnected(false);
        board.SetConnected(true);

        Assert.True(await contact.WaitAsync(moved: true, TimeSpan.FromMilliseconds(100), TimeProvider.System, Cancel));
    }

    [Fact]
    public async Task Should_FindTheBoard_WhenItConnectsInTime()
    {
        var board = new FakeRelayBoard(connected: false);
        using var contact = BoardContact.Watch(board);
        var wait = contact.WaitAsync(moved: true, WaitTimeout, TimeProvider.System, Cancel);

        board.SetConnected(true);

        Assert.True(await wait);
    }

    [Fact]
    public async Task Should_GiveUp_WhenTheBoardDoesNotAnswer()
    {
        var board = new FakeRelayBoard(connected: false);
        using var contact = BoardContact.Watch(board);

        Assert.False(await contact.WaitAsync(moved: false, TimeSpan.FromMilliseconds(100), TimeProvider.System, Cancel));
    }

    [Theory]
    [InlineData(8, 1, 0, "8 relays · 1 input")]
    [InlineData(4, 4, 1, "4 relays · 4 inputs · 1 analog input")]
    [InlineData(1, 0, 2, "1 relay · 2 analog inputs")]
    public void Should_DescribeWhatAModelHas(int relays, int inputs, int analog, string text)
    {
        Assert.Equal(text, SetupText.Capabilities(new BoardCapabilities(relays, inputs, ReportsChanges: true, analog)));
    }

    [Theory]
    [InlineData(6, 2, "6 in use, 2 hidden")]
    [InlineData(8, 0, "8 in use")]
    [InlineData(0, 4, "None in use, 4 hidden")]
    public void Should_SummariseTheRelaysInUse(int inUse, int hidden, string text)
    {
        Assert.Equal(text, SetupText.RelaysInUse(inUse, hidden));
    }

    private static async Task<List<EventLogEntry>> EventsAsync(TestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.OrderBy(e => e.Id).ToListAsync(Cancel);
    }

    private static TestDatabase CreateChoiceDatabase(Dictionary<string, string?>? settings = null)
    {
        return TestDatabase.Create((services, configuration) => services.AddBoards(configuration), settings);
    }

    private static async Task AddInstallationAsync(TestDatabase database, Installation installation)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Installations.Add(installation);
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }

    /// <summary>
    /// The setup guide's handler on a database of its own, with a board in memory with 8 relays in place of the app's
    /// board; the guide's choice of model still builds the real one behind it.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(TestDatabase database, FakeRelayBoard board)
        {
            Database = database;
            Board = board;
        }

        public TestDatabase Database { get; }

        public FakeRelayBoard Board { get; }

        public SetupHandler Setup => Database.Services.GetRequiredService<SetupHandler>();

        public static async Task<Rig> StartAsync(ILoggerProvider? logs = null, bool startRecorder = false)
        {
            var board = new FakeRelayBoard();
            var database = TestDatabase.Create((services, configuration) =>
            {
                if (logs is not null)
                {
                    services.AddSingleton(logs);
                }

                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
                services.AddBoards(configuration);
                services.AddEvents(configuration);
                services.AddSingleton<IRelayBoard>(board);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddSetup(configuration);
            });
            await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
            var rig = new Rig(database, board);
            if (startRecorder)
            {
                // Credits a failed ON to the person when the relay comes on all the same.
                rig._recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
                await rig._recorder.StartAsync(Cancel);
            }

            return rig;
        }

        private BoardEventRecorder? _recorder;

        public async ValueTask DisposeAsync()
        {
            if (_recorder is not null)
            {
                await _recorder.StopAsync(CancellationToken.None);
            }

            await Database.DisposeAsync();
        }
    }

    /// <summary>Keeps every log message, so a test can wait for one.</summary>
    private sealed class LogCollector : ILoggerProvider, ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<string> _messages = [];

        public bool Contains(string text)
        {
            lock (_lock)
            {
                return _messages.Exists(m => m.Contains(text, StringComparison.Ordinal));
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_lock)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
