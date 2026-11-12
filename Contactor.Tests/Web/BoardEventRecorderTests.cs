using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Groups;
using Contactor.Web;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Web;

public sealed class BoardEventRecorderTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogNothing_ForTheFirstStatusAfterStart()
    {
        await using var rig = await BoardTestRig.StartAsync(s => s.SetOutputsAsync(0b0000_0101));

        await rig.Simulator.SetOutputsAsync(0b0000_0101);
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.Empty(await rig.EventsAsync());
    }

    [Fact]
    public async Task Should_LogExternal_WhenARelayIsTurnedOnAtTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(4));

        await rig.WaitForEventsAsync(events => events.Count == 1);
        var entry = Assert.Single(await rig.EventsAsync());
        Assert.Equal((4, EventActors.External, EventActions.On), (entry.RelayId!.Value, entry.Actor, entry.Action));
    }

    [Fact]
    public async Task Should_NotLogTheAppsOwnCommandsTwice()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        await rig.Control.TurnAllOffAsync(Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        var events = await rig.EventsAsync();
        Assert.Equal([EventActions.On, EventActions.Off], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(Jane, e.Actor));
    }

    [Fact]
    public async Task Should_LogExternal_WhenTheExpectationHasExpired()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        // The app asked for "on" again while it was on; the board answers nothing, so that expectation lingers.
        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Simulator.SetOutputsAsync(0);
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(2));

        await rig.WaitForEventsAsync(events => events.Count(e => e.Actor == EventActors.External) == 2);
    }

    [Fact]
    public async Task Should_ForgetWhoTurnedItOn_WhenItIsTurnedOffAtTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        await rig.Simulator.SetOutputsAsync(0);

        await rig.WaitForEventsAsync(events => events.Any(e => e.Actor == EventActors.External));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    [Fact]
    public async Task Should_LogChangesMadeWhileDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync(s => s.SetOutputsAsync(Vm201Channel.Mask(1)));
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(5));
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        await rig.WaitForEventsAsync(events => events.Count == 2);
        var events = await rig.EventsAsync();
        Assert.Contains(events, e => e is { RelayId: 1, Action: EventActions.Off, Actor: EventActors.External });
        Assert.Contains(events, e => e is { RelayId: 5, Action: EventActions.On, Actor: EventActors.External });
    }

    [Fact]
    public async Task Should_LogTheInput_WhenItChanges()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Simulator.SetInputAsync(true);

        await rig.WaitForEventsAsync(events => events.Count == 1);
        var entry = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActions.InputChanged, """{"on":true}""", (int?)null, (int?)1), (entry.Action, entry.Details, entry.RelayId, entry.InputId));
    }

    // A board with several inputs, e.g. the VM204: each change is logged with its input.
    [Fact]
    public async Task Should_LogEveryInput_WithItsNumber()
    {
        var board = new Contactor.Tests.Boards.FakeRelayBoard(relayCount: 4, inputCount: 4);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddSingleton<Contactor.Boards.IRelayBoard>(board);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel, inputCount: 4);
        var recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            board.SetInput(3, on: true);
            board.SetInput(1, on: true);
            board.SetInput(3, on: false);

            await BoardTestRig.WaitUntilAsync(() => Events(database).Count == 3);
            Assert.Equal(
                [(3, """{"on":true}"""), (1, """{"on":true}"""), (3, """{"on":false}""")],
                Events(database).Select(e => (e.InputId ?? 0, e.Details ?? "")));
        }
        finally
        {
            await recorder.StopAsync(Cancel);
        }
    }

    // A reboot or an upgrade is a short gap: the inputs' times stay. A long one (a crash, a board that was off) doesn't.
    [Theory]
    [InlineData(2, false)]
    [InlineData(60, true)]
    public async Task Should_MarkALongGap_AtStart(int minutesAway, bool longGap)
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-minutesAway));

        await setup.StartAsync();

        var state = setup.State();
        Assert.Equal(longGap ? BoardTestRig.Start : null, state.LongGapEndedUtc);
        Assert.Equal(BoardTestRig.Start, state.LastContactUtc);
    }

    // At start the inputs are compared with what the log last says: a change made while the app was down is logged, as
    // noticed; an input that reads the same logs nothing.
    [Fact]
    public async Task Should_LogAnInputThatChangedWhileDown_AsNoticed()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-2));
        setup.Log(1, on: false);
        setup.Log(2, on: true);
        setup.Board.SetInput(1, on: true);
        setup.Board.SetInput(2, on: true);

        await setup.StartAsync();

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 3);
        var noticed = InputEvents(setup.Database)[2];
        Assert.Equal((1, """{"on":true,"noticed":true}"""), (noticed.InputId ?? 0, noticed.Details ?? ""));
    }

    // A change seen live is logged as it is; one made while contact was lost is logged at the reconnect, as noticed.
    [Fact]
    public async Task Should_MarkOnlyChangesMadeDuringALostContact_AsNoticed()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();

        setup.Board.SetInput(1, on: true);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 1);
        setup.Board.SetConnected(false);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        setup.Board.SetInput(1, on: false);
        setup.Board.SetConnected(true);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);

        Assert.Equal(["""{"on":true}""", """{"on":false,"noticed":true}"""], InputEvents(setup.Database).Select(e => e.Details));
    }

    // A lost contact longer than the limit, while the app runs, ends as a long gap; a short one doesn't.
    [Theory]
    [InlineData(2, false)]
    [InlineData(11, true)]
    public async Task Should_MarkALongLostContact(int minutesLost, bool longGap)
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        Assert.Null(setup.State().LongGapEndedUtc);

        setup.Board.SetConnected(false);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        setup.Time.Now += TimeSpan.FromMinutes(minutesLost);
        var back = setup.Time.Now.UtcDateTime;
        setup.Board.SetConnected(true);

        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == back);
        Assert.Equal(longGap ? back : null, setup.State().LongGapEndedUtc);
    }

    // A clock that went back (a Raspberry Pi after a power cut, before it reaches a time server) can't measure the gap.
    [Fact]
    public async Task Should_CountAClockThatWentBack_AsALongGap()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddHours(1));

        await setup.StartAsync();

        Assert.Equal(BoardTestRig.Start, setup.State().LongGapEndedUtc);
    }

    // The time of contact is saved every few minutes while there is contact, and not more often.
    [Fact]
    public async Task Should_SaveTheTimeOfContact_EveryFewMinutes()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();

        setup.Time.Now += BoardEventRecorder.ContactSaveInterval - TimeSpan.FromSeconds(1);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        Assert.Equal(BoardTestRig.Start, setup.State().LastContactUtc);

        setup.Time.Now += TimeSpan.FromSeconds(1);
        var due = setup.Time.Now.UtcDateTime;
        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == due);
    }

    // A save that fails when contact comes back loses nothing: the next status records the change and the short gap.
    [Fact]
    public async Task Should_StartOver_WhenTheSaveAfterALostContactFails()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        setup.Board.SetConnected(false);
        setup.Board.SetInput(1, on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");

        setup.Board.SetConnected(true);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        Assert.Empty(InputEvents(setup.Database));
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");
        setup.Board.SetInput(2, on: true);

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);
        Assert.Contains(InputEvents(setup.Database), e => e is { InputId: 1, Details: """{"on":true,"noticed":true}""" });
        Assert.Null(setup.State().LongGapEndedUtc);
    }

    private static List<EventLogEntry> InputEvents(TestDatabase database)
    {
        return [.. Events(database).Where(e => e.Action == EventActions.InputChanged)];
    }

    private static async Task<InputRig> InputRigAsync(DateTime lastContact)
    {
        var board = new Contactor.Tests.Boards.FakeRelayBoard(relayCount: 4, inputCount: 2);
        var time = new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start));
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<Contactor.Boards.IRelayBoard>(board);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel, inputCount: 2);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.BoardWatchStates.Add(new BoardWatchState { LastContactUtc = lastContact });
            await db.SaveChangesAsync(Cancel);
        }

        return new InputRig(board, database, time);
    }

    private sealed class InputRig : IAsyncDisposable
    {
        private BoardEventRecorder? _recorder;

        public InputRig(Contactor.Tests.Boards.FakeRelayBoard board, TestDatabase database, FixedTimeProvider time)
        {
            Board = board;
            Database = database;
            Time = time;
        }

        public FixedTimeProvider Time { get; }

        public Contactor.Tests.Boards.FakeRelayBoard Board { get; }

        public TestDatabase Database { get; }

        public async Task StartAsync()
        {
            _recorder = Database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
            await _recorder.StartAsync(Cancel);
            await BoardTestRig.WaitUntilAsync(() => State().LastContactUtc == BoardTestRig.Start);
        }

        public void Log(int input, bool on)
        {
            using var db = Database.Factory.CreateDbContext();
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = BoardTestRig.Start.AddHours(-1),
                InputId = input,
                Actor = EventActors.External,
                Action = EventActions.InputChanged,
                Details = on ? """{"on":true}""" : """{"on":false}""",
            });
            db.SaveChanges();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            await db.Database.ExecuteSqlRawAsync(sql, Cancel);
        }

        public BoardWatchState State()
        {
            using var db = Database.Factory.CreateDbContext();
            return db.BoardWatchStates.AsNoTracking().Single();
        }

        public async ValueTask DisposeAsync()
        {
            if (_recorder is not null)
            {
                await _recorder.StopAsync(CancellationToken.None);
            }

            await Database.DisposeAsync();
        }
    }

    private static List<EventLogEntry> Events(TestDatabase database)
    {
        using var db = database.Factory.CreateDbContext();
        return [.. db.EventLog.OrderBy(e => e.Id)];
    }

    [Fact]
    public async Task Should_LogLossAfter30SecondsAndTheReturn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        var lostAt = rig.Time.Now.UtcDateTime;

        rig.Time.Now += BoardEventRecorder.ConnectionLostAfter - TimeSpan.FromSeconds(1);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        Assert.Empty(await rig.EventsAsync());

        rig.Time.Now += TimeSpan.FromSeconds(1);
        await rig.WaitForEventsAsync(events => events.Count == 1);
        var lost = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActions.ConnectionLost, EventActors.System, lostAt), (lost.Action, lost.Actor, lost.TimestampUtc));

        rig.Simulator.RejectConnections = false;
        await rig.WaitForEventsAsync(events => events.Count == 2);
        var restored = (await rig.EventsAsync())[1];
        Assert.Equal(EventActions.ConnectionRestored, restored.Action);
        Assert.Equal("""{"afterSeconds":30}""", restored.Details);
    }

    [Fact]
    public async Task Should_LogNothing_WhenTheConnectionComesBackQuickly()
    {
        await using var rig = await BoardTestRig.StartAsync();
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        rig.Time.Now += BoardEventRecorder.ConnectionLostAfter * 2;
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);

        Assert.Empty(await rig.EventsAsync());
    }
}
