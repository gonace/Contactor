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
        Assert.Contains("Skip for now", html, StringComparison.Ordinal);
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

    [Fact]
    public async Task Should_CarryOnAtTheStepInTheAddress_AfterARestart()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/setup?step=3", Cancel));

        Assert.Contains("STEP 3 OF 5", html, StringComparison.Ordinal);
        Assert.Contains("Where is the board?", html, StringComparison.Ordinal);
        Assert.Contains("Velleman VM201 · its address on your network.", html, StringComparison.Ordinal);
        Assert.Contains("Go on without a connection", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ListEveryModel_OnStepTwo()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/setup?step=2", Cancel));

        Assert.Contains("Which relay board?", html, StringComparison.Ordinal);
        Assert.Contains("8 relays · 1 input", html, StringComparison.Ordinal);
        Assert.Contains("Velleman VM204", html, StringComparison.Ordinal);
        Assert.Matches("value=\"vm201\" checked", html);
    }

    [Fact]
    public async Task Should_NotOpenOnTheLastStep_FromTheAddress()
    {
        await using var app = new TestWebApp("Development", John, setUp: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/setup?step=5", Cancel));

        Assert.Contains("STEP 1 OF 5", html, StringComparison.Ordinal);
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
        var connection = database.Services.GetRequiredService<IBoardConnection>();
        var client = database.Services.GetRequiredService<Vm201Client>();
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
    public async Task Should_UseTheSavedModel_OverBoardType()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = database.ConnectionString,
            ["Board:Type"] = "vm201",
        };

        Assert.Null(PersistenceServiceCollectionExtensions.ReadSavedBoardType(Configuration(settings)));
        Assert.Equal(Vm201Model.Model, BuildBoards(settings).GetRequiredService<BoardModel>());

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Installations.Add(new Installation { BoardType = "vm204" });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Equal("vm204", PersistenceServiceCollectionExtensions.ReadSavedBoardType(Configuration(settings)));
        Assert.Equal(Vm204Model.Model, BuildBoards(settings).GetRequiredService<BoardModel>());
    }

    [Fact]
    public async Task Should_RefuseToStart_OnASavedModelItDoesNotKnow()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Installations.Add(new Installation { BoardType = "vm999" });
            await db.SaveChangesAsync(Cancel);
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildBoards(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = database.ConnectionString }));
        Assert.Contains("vm999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReadNoSavedModel_WithoutADatabase()
    {
        var folder = Path.Combine(Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(folder, "contactor.db")}" };

        Assert.Null(PersistenceServiceCollectionExtensions.ReadSavedBoardType(Configuration(settings)));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Should_SayWhetherTheAppMustRestart_ForTheChosenModel()
    {
        await using var rig = await Rig.StartAsync();

        Assert.False(await rig.Setup.ChooseBoardAsync("vm201", Cancel));
        Assert.True(await rig.Setup.ChooseBoardAsync(" VM204 ", Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Setup.ChooseBoardAsync("vm999", Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal("vm204", (await db.Installations.SingleAsync(Cancel)).BoardType);
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

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider BuildBoards(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBoards(Configuration(settings));
        return services.BuildServiceProvider();
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

    /// <summary>The setup guide's handler on a database of its own, with a board in memory running a VM201.</summary>
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

        public static async Task<Rig> StartAsync()
        {
            var board = new FakeRelayBoard();
            var database = TestDatabase.Create((services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
                services.AddSingleton(Vm201Model.Model);
                services.AddEvents(configuration);
                services.AddSingleton<IRelayBoard>(board);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddSetup(configuration);
            });
            await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
            return new Rig(database, board);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
