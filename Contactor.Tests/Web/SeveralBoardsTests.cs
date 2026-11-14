using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// Two boards in one app: the simulator (board A, 8 relays) and a board in memory (board B, 4 relays and 2 inputs).
/// Every safety rule holds per board: a command reaches its own board on its own channel, a board that is lost holds
/// up nothing on the other, and nothing is queued for it. The rig starts on Wednesday 4 November 2026 at 13:00 UTC,
/// 14:00 local.
/// </summary>
public sealed class SeveralBoardsTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // As on the Group screen: the fan first and running on after the heat, the heat after 3 s.
    private static GroupDraft FanHeater(int fan, int heat) => new(
        "Fan heater",
        GroupIcons.Fan,
        120,
        [new GroupMemberDraft(fan, "Fan", 3, 300), new GroupMemberDraft(heat, "Heat", 3, 0)]);

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_SendEveryCommand_ToTheRelaysOwnBoardAndChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout, secondBoard: true);
        var second = rig.Second!;

        await rig.Control.TurnOnAsync(rig.SecondRelayId(3), Jane, Cancel);
        Assert.Equal(ChannelMask.Of(3), second.State.Relays);
        Assert.Equal(0, rig.Simulator.Outputs);

        await rig.Control.TurnOnAsync(rig.RelayId(2), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b10);
        Assert.Equal([(ChannelMask.Of(3), true)], second.Sent);

        await rig.Control.TurnOffAsync(rig.SecondRelayId(3), Jane, Cancel);
        Assert.Equal(ChannelMask.None, second.State.Relays);
        Assert.Equal(0b10, rig.Simulator.Outputs);

        // Each event is about the relay's own board.
        var events = (await rig.EventsAsync()).Where(e => e.Actor == Jane).ToList();
        Assert.Equal(
            [(rig.SecondBoardId, rig.SecondRelayId(3), EventActions.On), (rig.Live.BoardId, rig.RelayId(2), EventActions.On), (rig.SecondBoardId, rig.SecondRelayId(3), EventActions.Off)],
            events.Select(e => (e.BoardId!.Value, e.RelayId!.Value, e.Action)));
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_TripTheInterlock_OnlyOnItsOwnBoard(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout, secondBoard: true);
        var second = rig.Second!;
        var shop = await CreateGroupAsync(rig, FanHeater(rig.RelayId(1), rig.RelayId(2)));
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b100);
        await StartFullyAsync(rig, barn);
        Assert.Equal(ChannelMask.Of([1, 2]), second.State.Relays);

        // The barn's fan drops at its board: its heat goes off at once and the fan comes back to run on.
        second.SetRelay(1, on: false);
        await rig.Groups.StepAsync(Cancel);

        Assert.Equal(ChannelMask.Of(1), second.State.Relays);
        var trip = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
        Assert.Equal((rig.SecondBoardId, barn), (trip.BoardId!.Value, trip.GroupId!.Value));

        // Board A, connected all along, is untouched: its relay outside the group stays on, its group is idle, and
        // nothing more was sent to it.
        Assert.Equal(0b100, rig.Simulator.Outputs);
        Assert.True(rig.Client.State.IsOn(3));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.GroupId == shop && e.Action != EventActions.GroupCreate);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.BoardId == rig.Live.BoardId && e.Action is not EventActions.GroupCreate and not EventActions.On);
    }

    // Each board's sequencer listens to its own board: a status from board B that breaks the interlock trips it there,
    // with no step asked for, while board A's loop runs on.
    [Fact]
    public async Task Should_TripTheInterlock_OnTheBoardsOwnStatus()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b100);
        await StartFullyAsync(rig, barn);
        var sequencer = await rig.StartServiceAsync<GroupSequencer>();
        await BoardTestRig.WaitUntilAsync(() => sequencer.RunningLoops == 2);

        second.SetRelay(1, on: false);

        await BoardTestRig.WaitUntilAsync(() => second.State.Relays == ChannelMask.Of(1));
        await rig.WaitForEventsAsync(events => events.Any(e => e.Action == EventActions.InterlockTrip && e.BoardId == rig.SecondBoardId));
        Assert.Equal(0b100, rig.Simulator.Outputs);
        Assert.True(rig.Client.State.IsOn(3));
    }

    // The interlock is checked first on every status: each board's sequencer listens before anything else does, so it
    // is subscribed first on every board, before its event recorder and its scheduler. Auto-off and the input actions
    // watch each board too, but listen to no status: auto-off reads the board's state at each check, and the input
    // actions get their changes from the recorder.
    [Fact]
    public async Task Should_WatchEachBoard_WithItsGroupsFirst()
    {
        await using var app = new TestWebApp();

        var watchers = app.Services.GetServices<IBoardWatcher>().Select(w => w.GetType()).ToList();

        Assert.Equal([typeof(AutoOffService), typeof(GroupSequencer), typeof(BoardEventRecorder), typeof(InputActionService), typeof(SchedulerService)], watchers);
    }

    // The boards' connections start only once every hosted service has started. An input that closes while the app is
    // still starting (here: while the last service is starting) happened before the app watched: the board's first
    // answer shows it closed, so it is logged as noticed and its action doesn't run. Were the board to connect before,
    // the close would be seen as a change and its action run late, once the app runs. The first change after start is
    // seen as it happens, and its action runs.
    [Fact]
    public async Task Should_ConnectTheBoards_OnlyOnceTheAppHasStarted()
    {
        await using var simulator = new Contactor.Boards.Vm201.Vm201Simulator();
        simulator.Start();
        HeldStart? held = null;
        await using var app = new TestWebApp(
            settings: new Dictionary<string, string?> { ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            configureServices: services => services.AddHostedService(sp => held = new HeldStart(sp, simulator)),
            setUp: false);
        app.SaveBeforeStart(new Installation { SetupDoneUtc = DateTime.UtcNow, AccessType = "local", SignInType = "none" }, boardType: "vm201");
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        Assert.NotNull(held);
        Assert.True(held.Ran);

        await BoardTestRig.WaitUntilAsync(() => InputChanges(factory).Count == 2);
        Assert.Equal("""{"on":true,"noticed":true}""", InputChanges(factory)[1]);

        // Live: the input opens, and its action turns relay 5 on. Relay 4, the close's, never came on.
        await simulator.SetInputAsync(false);
        await BoardTestRig.WaitUntilAsync(() => (simulator.Outputs & 0b10000) != 0);
        Assert.Equal(0b10000, simulator.Outputs);
        Assert.Equal(["""{"on":false}""", """{"on":true,"noticed":true}""", """{"on":false}"""], InputChanges(factory));
        Assert.True(registry.All[0].State.IsConnected);
    }

    private static List<string?> InputChanges(IDbContextFactory<ContactorDbContext> factory)
    {
        using var db = factory.CreateDbContext();
        return [.. db.EventLog.Where(e => e.Action == EventActions.InputChanged).OrderBy(e => e.Id).Select(e => e.Details)];
    }

    // The last service to start: gives input 1 its actions (closed: relay 4 on; opened: relay 5 on) and logs it open,
    // as the app last saw it, then closes the
    // input at the board while the app is still starting. Were the board connected by then, it waits for the board to
    // report the close, so the app would have seen it happen.
    private sealed class HeldStart : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly Contactor.Boards.Vm201.Vm201Simulator _simulator;

        public HeldStart(IServiceProvider services, Contactor.Boards.Vm201.Vm201Simulator simulator)
        {
            _services = services;
            _simulator = simulator;
        }

        public bool Ran { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var board = _services.GetRequiredService<BoardRegistry>().All[0];
            await using (var db = await _services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(cancellationToken))
            {
                var input = await db.Inputs.SingleAsync(i => i.Channel == 1, cancellationToken);
                var relay4 = await db.Relays.SingleAsync(r => r.Channel == 4, cancellationToken);
                var relay5 = await db.Relays.SingleAsync(r => r.Channel == 5, cancellationToken);
                db.InputActions.Add(new InputAction { InputId = input.Id, RelayId = relay4.Id, WhenClosed = true });
                db.InputActions.Add(new InputAction { InputId = input.Id, RelayId = relay5.Id, WhenOpened = true });

                // As the app last saw it: open.
                db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow.AddHours(-1), InputId = input.Id, Actor = EventActors.External, Action = EventActions.InputChanged, Details = """{"on":false}""" });
                await db.SaveChangesAsync(cancellationToken);
            }

            // A board connected this early answers within a moment; one that waits for the app never does.
            await WaitAsync(() => board.State.IsConnected, cancellationToken);
            await _simulator.SetInputAsync(true);
            if (board.State.IsConnected)
            {
                await WaitAsync(() => board.State.Inputs.Contains(1), cancellationToken);
            }

            Ran = true;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            for (var i = 0; i < 100 && !condition(); i++)
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    // Each board's inputs' actions run on their own queue: a command board B doesn't finish holds up only B's.
    [Fact]
    public async Task Should_RunEachBoardsInputActions_WhileAnotherBoardIsSlow()
    {
        await using var boards = await StuckBoardsAsync();
        var handler = boards.Database.Services.GetRequiredService<InputActionsHandler>();
        int InputOf(bool onB)
        {
            using var db = boards.Database.Factory.CreateDbContext();
            return db.Inputs.Single(i => (i.BoardId == boards.BoardB) == onB && i.Channel == 1).Id;
        }

        Assert.Null(await handler.SaveAsync(InputOf(onB: true), [new InputActionRow(new InputActionTarget(boards.RelayOfB(2), 2, null, "Relay 2"), true, null)], Cancel));
        Assert.Null(await handler.SaveAsync(InputOf(onB: false), [new InputActionRow(new InputActionTarget(boards.RelayOfA(3), 3, null, "Relay 3"), true, null)], Cancel));
        await boards.StartAsync<InputActionService>();
        var feed = boards.Database.Services.GetRequiredService<InputChangeFeed>();

        // B's input: its relay switches and B never finishes the command.
        feed.Enqueue(InputOf(onB: true), closed: true);
        await BoardTestRig.WaitUntilAsync(() => boards.B.State.IsOn(2));

        feed.Enqueue(InputOf(onB: false), closed: true);
        await BoardTestRig.WaitUntilAsync(() => boards.A.State.IsOn(3));
    }

    // Each board's schedules run on their own loop: a command board B doesn't finish holds up only B's minutes. Board
    // A's run starts and ends on time, its live OffTime within the minute.
    [Fact]
    public async Task Should_RunEachBoardsSchedulesOnTime_WhileAnotherBoardIsSlow()
    {
        await using var boards = await StuckBoardsAsync();
        await boards.AddScheduleAsync(boards.RelayOfA(1), new TimeOnly(11, 2), new TimeOnly(11, 3));
        await boards.AddScheduleAsync(boards.RelayOfB(1), new TimeOnly(11, 1), new TimeOnly(11, 30));
        var scheduler = await boards.StartAsync<SchedulerService>();
        await BoardTestRig.WaitUntilAsync(() => scheduler.RunningLoops == 2 && boards.Time.PendingTimers == 2);

        // 11:01 local: B's ON goes out and B never finishes it.
        boards.Time.Advance(TimeSpan.FromSeconds(61));
        await BoardTestRig.WaitUntilAsync(() => boards.B.State.IsOn(1) && boards.Time.PendingTimers == 1);

        boards.Time.Advance(TimeSpan.FromMinutes(1));
        await BoardTestRig.WaitUntilAsync(() => boards.A.State.IsOn(1) && boards.Time.PendingTimers == 1);
        boards.Time.Advance(TimeSpan.FromMinutes(1));
        await BoardTestRig.WaitUntilAsync(() => !boards.A.State.IsOn(1));
        Assert.Equal([(ChannelMask.Of(1), true), (ChannelMask.Of(1), false)], boards.A.Sent);
    }

    // Each board's auto-off runs on its own loop: an OFF board B doesn't finish holds up only B's checks.
    [Fact]
    public async Task Should_TurnOffEachBoardsRelaysOnTime_WhileAnotherBoardIsSlow()
    {
        await using var boards = await StuckBoardsAsync(stuck: false);
        var control = boards.Database.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(boards.RelayOfB(2), Jane, Cancel);
        boards.Time.Advance(TimeSpan.FromMinutes(10));
        await control.TurnOnAsync(boards.RelayOfA(3), Jane, Cancel);
        boards.Stick();
        var autoOff = await boards.StartAsync<AutoOffService>();
        await BoardTestRig.WaitUntilAsync(() => autoOff.RunningLoops == 2 && boards.Time.PendingTimers == 2);

        // B's auto-off is due first: its OFF goes out and B never finishes it.
        boards.Time.Advance(TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes - 9));
        await BoardTestRig.WaitUntilAsync(() => !boards.B.State.IsOn(2) && boards.Time.PendingTimers == 1);
        Assert.True(boards.A.State.IsOn(3));

        boards.Time.Advance(TimeSpan.FromMinutes(10));
        await BoardTestRig.WaitUntilAsync(() => !boards.A.State.IsOn(3));
    }

    [Fact]
    public async Task Should_KeepBoardAWorking_WhileBoardBIsLost()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        var shop = await CreateGroupAsync(rig, FanHeater(rig.RelayId(1), rig.RelayId(2)));
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));
        await AddScheduleAsync(rig, rig.RelayId(3), new TimeOnly(13, 30), new TimeOnly(15, 0));
        await AddScheduleAsync(rig, rig.SecondRelayId(3), new TimeOnly(13, 30), new TimeOnly(15, 0));
        await SaveInputAsync(rig, rig.InputId(1), rig.RelayId(4), channel: 4);

        second.SetConnected(false);

        // Board B refuses at once, and nothing reaches it.
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Control.TurnOnAsync(rig.SecondRelayId(4), Jane, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Groups.StartGroupAsync(barn, Jane, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Control.TurnAllOffAsync(rig.SecondBoardId, Jane, Cancel));

        // Board A: a press, a group, its schedule and its input, as always.
        await rig.Control.TurnOnAsync(rig.RelayId(5), Jane, Cancel);
        await rig.Groups.StartGroupAsync(shop, Jane, Cancel);
        await rig.Scheduler.TickAsync(Cancel);
        await InputActions(rig).RunAsync(new InputChanged(rig.InputId(1), Closed: true), Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b11101);
        Assert.Equal(BoardTestRig.Start, (await RuntimeAsync(rig, rig.Live.BoardId)).LastTickUtc);
        Assert.Null((await RuntimeAsync(rig, rig.SecondBoardId)).LastTickUtc);
        Assert.Empty(second.Sent);

        // While it was lost, someone switched the barn's heat on at its board, without the fan.
        second.SetRelay(2, on: true);
        second.SetConnected(true);
        var progressOfA = (await RuntimeAsync(rig, rig.Live.BoardId)).LastTickUtc;
        var outputsOfA = rig.Simulator.Outputs;

        // On B's reconnect: B's interlock first, then B's catch-up of its schedule.
        await rig.Groups.For(rig.SecondBoardId)!.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of(1), second.State.Relays);
        await rig.Scheduler.For(rig.SecondBoardId)!.TickAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 3]), second.State.Relays);

        // Nothing that was refused while it was lost went out later: only the trip's commands and the schedule's ON.
        Assert.Equal([(ChannelMask.Of(2), false), (ChannelMask.Of(1), true), (ChannelMask.Of(3), true)], second.Sent);
        Assert.Equal(BoardTestRig.Start, (await RuntimeAsync(rig, rig.SecondBoardId)).LastTickUtc);
        Assert.Equal(progressOfA, (await RuntimeAsync(rig, rig.Live.BoardId)).LastTickUtc);
        Assert.Equal(outputsOfA, rig.Simulator.Outputs);
        var trip = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
        Assert.Equal(rig.SecondBoardId, trip.BoardId);
    }

    // A board that is lost holds back only its own catch-up: the other's minutes move on.
    [Fact]
    public async Task Should_CatchUpEachBoardsSchedules_OnItsOwn()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        await AddScheduleAsync(rig, rig.RelayId(1), new TimeOnly(14, 10), new TimeOnly(14, 20));
        await AddScheduleAsync(rig, rig.SecondRelayId(1), new TimeOnly(14, 10), new TimeOnly(14, 20));
        await rig.Scheduler.TickAsync(Cancel);

        second.SetConnected(false);
        rig.Time.Now = BoardTestRig.Start.AddMinutes(15);
        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b1);
        Assert.Equal(BoardTestRig.Start.AddMinutes(15), (await RuntimeAsync(rig, rig.Live.BoardId)).LastTickUtc);
        Assert.Equal(BoardTestRig.Start, (await RuntimeAsync(rig, rig.SecondBoardId)).LastTickUtc);

        // Back after its window ended: B's missed ON isn't sent; its run is over. A goes off at its own OffTime.
        rig.Time.Now = BoardTestRig.Start.AddMinutes(25);
        second.SetConnected(true);
        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0);
        Assert.Empty(second.Sent);
        Assert.Equal(BoardTestRig.Start.AddMinutes(25), (await RuntimeAsync(rig, rig.SecondBoardId)).LastTickUtc);
    }

    [Fact]
    public async Task Should_TurnOffEachBoardsDueRelays_OnlyWhileItIsConnected()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await rig.Control.TurnOnAsync(rig.SecondRelayId(2), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));
        second.SetConnected(false);
        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes + 1);

        Assert.Equal([rig.RelayId(3)], await AutoOff(rig).CheckAsync(Cancel));
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0);
        Assert.True(second.State.IsOn(2));
        Assert.NotNull((await RelayRuntimeAsync(rig, rig.SecondRelayId(2))).AutoOffAtUtc);

        second.SetConnected(true);
        Assert.Equal([rig.SecondRelayId(2)], await AutoOff(rig).CheckAsync(Cancel));

        Assert.Equal(ChannelMask.None, second.State.Relays);
        var autoOffs = (await rig.EventsAsync()).Where(e => e.Actor == EventActors.AutoOff).ToList();
        Assert.Equal([(rig.Live.BoardId, rig.RelayId(3)), (rig.SecondBoardId, rig.SecondRelayId(2))], autoOffs.Select(e => (e.BoardId!.Value, e.RelayId!.Value)));
    }

    [Fact]
    public async Task Should_LogEachBoardsOwnChanges_WithItsBoard()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;

        second.SetRelay(3, on: true);
        await rig.Simulator.SwitchRelayAsync(3, turnOn: true);

        await rig.WaitForEventsAsync(events => events.Count(e => e.Actor == EventActors.External) == 2);
        var external = (await rig.EventsAsync()).Where(e => e.Actor == EventActors.External).OrderBy(e => e.BoardId).ToList();
        Assert.Equal(
            [(rig.Live.BoardId, rig.RelayId(3)), (rig.SecondBoardId, rig.SecondRelayId(3))],
            external.Select(e => (e.BoardId!.Value, e.RelayId!.Value)));
        Assert.Equal(EventActors.External, (await RelayRuntimeAsync(rig, rig.SecondRelayId(3))).TurnedOnBy);

        // Only the board that is lost is logged as lost.
        second.SetConnected(false);
        rig.Time.Now += BoardEventRecorder.ConnectionLostAfter * 2;
        await rig.WaitForEventsAsync(events => events.Any(e => e.Action == EventActions.ConnectionLost));

        // Board A kept its contact throughout, so its recorder has no loss to log, however long it runs.
        Assert.True(rig.Live.State.IsConnected);
        var lost = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.ConnectionLost);
        Assert.Equal(rig.SecondBoardId, lost.BoardId);
    }

    [Fact]
    public async Task Should_TurnAllOff_OnlyOnTheBoardAskedFor()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));
        await StartFullyAsync(rig, barn);
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await rig.Control.TurnOnAsync(rig.SecondRelayId(4), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b100);

        await rig.Control.TurnAllOffAsync(rig.SecondBoardId, Jane, Cancel);

        // B's single relay off, its group stopping (the heat off, the fan running on); A's relay still on.
        Assert.Equal(ChannelMask.Of(1), second.State.Relays);
        Assert.Equal(0b100, rig.Simulator.Outputs);
        var allOff = Assert.Single(await rig.EventsAsync(), e => e.Details == """{"allOff":true}""");
        Assert.Equal((rig.SecondBoardId, rig.SecondRelayId(4)), (allOff.BoardId!.Value, allOff.RelayId!.Value));
        Assert.NotNull((await RelayRuntimeAsync(rig, rig.RelayId(3))).OnSinceUtc);
    }

    [Fact]
    public async Task Should_TurnEveryBoardOff_AndTellWhichCouldNotBeReached()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await rig.Control.TurnOnAsync(rig.SecondRelayId(4), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b100);
        second.SetConnected(false);

        var unreachable = await rig.Control.TurnOffAllBoardsAsync(Jane, Cancel);

        Assert.Equal([rig.SecondBoardId], unreachable);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0);
        Assert.True(second.State.IsOn(4));

        // Nothing waits for it: back again, it is sent nothing.
        second.SetConnected(true);
        await rig.Groups.StepAsync(Cancel);
        await AutoOff(rig).CheckAsync(Cancel);
        Assert.Equal([(ChannelMask.Of(4), true)], second.Sent);
        Assert.Equal([rig.Live.BoardId], (await rig.EventsAsync()).Where(e => e.Details == """{"allOff":true}""").Select(e => e.BoardId!.Value));

        // Both answer: none is left out.
        Assert.Empty(await rig.Control.TurnOffAllBoardsAsync(Jane, Cancel));
        Assert.Equal(ChannelMask.None, second.State.Relays);
    }

    [Fact]
    public async Task Should_RefuseAGroup_WithRelaysOnTwoBoards()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var groups = rig.GroupSetup;

        var (id, error) = await groups.CreateAsync(FanHeater(rig.RelayId(1), rig.SecondRelayId(2)), Jane, Cancel);
        Assert.Null(id);
        Assert.Equal("All the relays of a group must be on one board.", error);
        Assert.Equal("All the relays of a group must be on one board.", (await groups.CreateAsync(FanHeater(rig.SecondRelayId(1), rig.RelayId(2)), Jane, Cancel)).Error);

        // A group takes its first relay's board, and only relays of that board can join it later.
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));
        Assert.Equal(rig.SecondBoardId, (await groups.GetAsync(barn, Cancel))!.BoardId);
        var moved = new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupMemberDraft(rig.SecondRelayId(1), "Fan"), new GroupMemberDraft(rig.RelayId(3), "Heat")]);
        Assert.Equal("All the relays of a group must be on one board.", await groups.SaveAsync(barn, moved, Jane, Cancel));
        Assert.Equal([rig.SecondRelayId(3), rig.SecondRelayId(4)], (await groups.FreeRelaysAsync(rig.SecondBoardId, Cancel)).Select(r => r.Id).Order());
        var candidates = await groups.CandidatesAsync(Cancel);
        Assert.Equal(
            [.. Enumerable.Range(1, 8).Select(c => (rig.Live.BoardId, rig.RelayId(c))), .. Enumerable.Range(1, 4).Select(c => (rig.SecondBoardId, rig.SecondRelayId(c)))],
            candidates.Select(c => (c.BoardId, c.RelayId)));
    }

    [Fact]
    public void Should_OfferOnlyTheFirstPickedRelaysBoard_InNewGroup()
    {
        var wizard = new GroupWizard();

        wizard.Toggle(5, "Fan", boardId: 2);
        wizard.Toggle(1, "Heat", boardId: 1);
        wizard.Toggle(6, "Heat", boardId: 2);

        Assert.Equal([5, 6], wizard.Members.Select(m => m.RelayId));
        Assert.True(wizard.FitsBoard(2));
        Assert.False(wizard.FitsBoard(1));
    }

    [Fact]
    public async Task Should_LetAnInputControl_OnlyItsOwnBoard()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        var handler = rig.Database.Services.GetRequiredService<InputActionsHandler>();
        var barn = await CreateGroupAsync(rig, FanHeater(rig.SecondRelayId(1), rig.SecondRelayId(2)));

        // Board A's input is offered A's relays only; the barn's group and relays are B's input's.
        var relaysOfB = Enumerable.Range(1, 4).Select(rig.SecondRelayId).ToList();
        Assert.DoesNotContain(await handler.TargetsAsync(rig.InputId(1), Cancel), t => t.GroupId == barn || (t.RelayId is { } id && relaysOfB.Contains(id)));
        Assert.Equal(
            [$"group:{barn}", $"relay:{rig.SecondRelayId(3)}", $"relay:{rig.SecondRelayId(4)}"],
            (await handler.TargetsAsync(rig.SecondInputId(1), Cancel)).Select(t => t.Key));

        // Refused across boards, both a relay and a group.
        Assert.Equal(
            "That relay is on another board: an input only controls its own board's.",
            await handler.SaveAsync(rig.InputId(1), [new InputActionRow(new InputActionTarget(rig.SecondRelayId(3), 3, null, "Relay 3"), true, null)], Cancel));
        Assert.Equal(
            "That group is on another board: an input only controls its own board's.",
            await handler.SaveAsync(rig.InputId(1), [new InputActionRow(new InputActionTarget(null, null, barn, "Fan heater"), true, null)], Cancel));

        // Each input acts on its own board.
        await SaveInputAsync(rig, rig.SecondInputId(1), rig.SecondRelayId(3), channel: 3);
        await SaveInputAsync(rig, rig.InputId(1), rig.RelayId(4), channel: 4);
        await InputActions(rig).RunAsync(new InputChanged(rig.SecondInputId(1), Closed: true), Cancel);
        Assert.Equal(ChannelMask.Of(3), second.State.Relays);
        Assert.Equal(0, rig.Simulator.Outputs);
        await InputActions(rig).RunAsync(new InputChanged(rig.InputId(1), Closed: true), Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b1000);
        Assert.Single(second.Sent);
    }

    // An action across boards saved before this rule (or by hand) does nothing: an input never switches another board.
    [Fact]
    public async Task Should_SkipAnInputsAction_OnAnotherBoard()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.InputActions.Add(new InputAction { InputId = rig.InputId(1), RelayId = rig.SecondRelayId(3), WhenClosed = true });
            await db.SaveChangesAsync(Cancel);
        }

        await InputActions(rig).RunAsync(new InputChanged(rig.InputId(1), Closed: true), Cancel);

        Assert.Empty(rig.Second!.Sent);
    }

    // A board's watch ends with its live board: its parts leave every service and their loops and subscriptions end;
    // the other board's go on.
    [Fact]
    public async Task Should_EndABoardsWatch_WhenItsLiveBoardIsDisposed()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var second = rig.Second!;
        var sequencer = await rig.StartServiceAsync<GroupSequencer>();
        var recorder = rig.Database.Services.GetRequiredService<BoardEventRecorder>();
        await BoardTestRig.WaitUntilAsync(() => sequencer.RunningLoops == 2 && recorder.RunningLoops == 2);
        Assert.True(second.HasListeners);

        await rig.SecondLive.DisposeAsync();

        Assert.Null(sequencer.For(rig.SecondBoardId));
        Assert.Null(recorder.For(rig.SecondBoardId));
        Assert.Null(rig.Scheduler.For(rig.SecondBoardId));
        Assert.False(second.HasListeners);
        await BoardTestRig.WaitUntilAsync(() => sequencer.RunningLoops == 1 && recorder.RunningLoops == 1);
        Assert.NotNull(sequencer.For(rig.Live.BoardId));
        await rig.Control.TurnOnAsync(rig.RelayId(1), Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b1);
    }

    // Two boards in memory on a clock whose timers only fire when the test moves it; board B's commands switch its
    // relays and then never finish, until the test lets them (on dispose).
    private static async Task<StuckBoards> StuckBoardsAsync(bool stuck = true)
    {
        var time = new ManualTimeProvider();
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(time);
            services.AddBoards(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddEvents(configuration);
            services.AddInputs(configuration);
            services.AddSchedules(configuration);
        });
        var boards = new StuckBoards(database, time);
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 1, Cancel);
        await database.Services.UseFakeBoardAsync(boards.A);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var row = new Board { Name = "Barn", SortOrder = 2 };
            db.Boards.Add(row);
            await db.SaveChangesAsync(Cancel);
            boards.BoardB = row.Id;
        }

        await database.Services.SeedBoardAsync(boards.BoardB, relayCount: 4, inputCount: 1, Cancel);
        await database.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(boards.B).Build(boards.BoardB), Cancel);
        if (stuck)
        {
            boards.Stick();
        }

        return boards;
    }

    private sealed class StuckBoards : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IHostedService> _started = [];

        public StuckBoards(TestDatabase database, ManualTimeProvider time)
        {
            Database = database;
            Time = time;
        }

        public TestDatabase Database { get; }

        public ManualTimeProvider Time { get; }

        public FakeRelayBoard A { get; } = new(relayCount: 4);

        public FakeRelayBoard B { get; } = new(relayCount: 4);

        public int BoardB { get; set; }

        // From now on, B's commands switch its relays and then wait.
        public void Stick()
        {
            B.AfterSwitch = () => _release.Task;
        }

        public int RelayOfA(int channel)
        {
            using var db = Database.Factory.CreateDbContext();
            return db.Relays.Single(r => r.BoardId != BoardB && r.Channel == channel).Id;
        }

        public int RelayOfB(int channel)
        {
            using var db = Database.Factory.CreateDbContext();
            return db.Relays.Single(r => r.BoardId == BoardB && r.Channel == channel).Id;
        }

        public async Task AddScheduleAsync(int relayId, TimeOnly on, TimeOnly off)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            db.Schedules.Add(new Schedule { RelayId = relayId, Days = ScheduleDays.All, OnTime = on, OffTime = off, IsEnabled = true, CreatedBy = Jane });
            await db.SaveChangesAsync(Cancel);
        }

        public async Task<T> StartAsync<T>()
            where T : IHostedService
        {
            var service = Database.Services.GetServices<IHostedService>().OfType<T>().Single();
            await service.StartAsync(Cancel);
            _started.Add(service);
            return service;
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            foreach (var service in Enumerable.Reverse(_started))
            {
                await service.StopAsync(CancellationToken.None);
            }

            await Database.DisposeAsync();
        }
    }

    private static async Task<int> CreateGroupAsync(BoardTestRig rig, GroupDraft draft)
    {
        var (id, error) = await rig.GroupSetup.CreateAsync(draft, Jane, Cancel);
        Assert.Null(error);
        return id!.Value;
    }

    // Starts a group and steps it until both its relays are on: the fan, then the heat after its 3 s.
    private static async Task StartFullyAsync(BoardTestRig rig, int groupId)
    {
        await rig.Groups.StartGroupAsync(groupId, Jane, Cancel);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
    }

    private static async Task AddScheduleAsync(BoardTestRig rig, int relayId, TimeOnly on, TimeOnly off)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule { RelayId = relayId, Days = ScheduleDays.All, OnTime = on, OffTime = off, IsEnabled = true, CreatedBy = Jane });
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task SaveInputAsync(BoardTestRig rig, int inputId, int relayId, int channel)
    {
        var handler = rig.Database.Services.GetRequiredService<InputActionsHandler>();
        Assert.Null(await handler.SaveAsync(inputId, [new InputActionRow(new InputActionTarget(relayId, channel, null, $"Relay {channel}"), true, null)], Cancel));
    }

    private static InputActionService InputActions(BoardTestRig rig)
    {
        return rig.Database.Services.GetServices<IHostedService>().OfType<InputActionService>().Single();
    }

    private static AutoOffService AutoOff(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<AutoOffService>();
    }

    private static async Task<BoardRuntime> RuntimeAsync(BoardTestRig rig, int boardId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.BoardRuntimes.AsNoTracking().SingleAsync(r => r.BoardId == boardId, Cancel);
    }

    private static async Task<RelayRuntime> RelayRuntimeAsync(BoardTestRig rig, int relayId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }
}
