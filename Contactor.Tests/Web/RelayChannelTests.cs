using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// A relay's id is only the database's key; the board goes by its channel. Every test runs on a database whose ids are
/// the channels (<see cref="RelayLayout.Same"/>) and on one whose ids are not (<see cref="RelayLayout.Apart"/>: from
/// 100 up, in reverse), and checks what reaches the board's channels, not only the ids.
/// </summary>
public sealed partial class RelayChannelTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static GroupDraft FanHeater(BoardTestRig rig) => new(
        "Fan heater – Lathe",
        GroupIcons.Fan,
        120,
        [new GroupMemberDraft(rig.RelayId(1), "Fan", 3, 300), new GroupMemberDraft(rig.RelayId(2), "Heat", 3, 0)]);

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_SwitchTheRelaysChannel_OnAndOff(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);

        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);

        await WaitForChannelsAsync(rig, 3);
        var runtime = await RuntimeAsync(rig, rig.RelayId(3));
        Assert.Equal((Jane, (DateTime?)BoardTestRig.Start.AddMinutes(Relay.DefaultMaxOnMinutes)), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));

        await rig.Control.TurnOffAsync(rig.RelayId(3), Jane, Cancel);

        await WaitForChannelsAsync(rig);
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(100, Cancel);
        var events = await rig.EventsAsync();
        Assert.Equal([(EventActions.On, rig.RelayId(3)), (EventActions.Off, rig.RelayId(3))], events.Select(e => (e.Action, e.RelayId ?? 0)));
        Assert.All(events, e => Assert.Equal(Jane, e.Actor));
        Assert.Null((await RuntimeAsync(rig, rig.RelayId(3))).OnSinceUtc);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_PulseTheRelaysChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);

        // The longest pulse, so the relay is still on when a slow machine gets to look: the simulator times it in real time.
        await rig.Control.PulseAsync(rig.RelayId(5), RelayControl.MaxPulse, Jane, Cancel);

        // The simulator reads the packet on its own loop, maybe after the command returned.
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Pulsing == ChannelMask.Of(5));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(5));
        var pulse = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActions.Pulse, (int?)rig.RelayId(5)), (pulse.Action, pulse.RelayId));
        Assert.Equal(Jane, (await RuntimeAsync(rig, rig.RelayId(5))).TurnedOnBy);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_TurnAllOff_AndLogEachRelayThatWasOn(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await rig.Control.TurnOnAsync(rig.RelayId(2), Jane, Cancel);
        await rig.Control.TurnOnAsync(rig.RelayId(6), Jane, Cancel);
        await WaitForChannelsAsync(rig, 2, 6);

        await rig.Control.TurnAllOffAsync(rig.Live.BoardId, Jane, Cancel);

        await WaitForChannelsAsync(rig);
        var off = (await rig.EventsAsync()).Where(e => e.Action == EventActions.Off).ToList();
        Assert.Equal(new[] { rig.RelayId(2), rig.RelayId(6) }.Order(), off.Select(e => e.RelayId ?? 0).Order());
        Assert.All(off, e => Assert.Equal("""{"allOff":true}""", e.Details));
        Assert.Null((await RuntimeAsync(rig, rig.RelayId(2))).OnSinceUtc);
        Assert.Null((await RuntimeAsync(rig, rig.RelayId(6))).OnSinceUtc);
    }

    // The ids from 101 up leave the channels' numbers free: a channel's number is no relay's id, and nothing is sent.
    [Fact]
    public async Task Should_RefuseAChannelsNumber_ThatIsNoRelaysId()
    {
        await using var rig = await BoardTestRig.StartAsync(layout: RelayLayout.Apart);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Control.TurnOnAsync(3, Jane, Cancel));

        Assert.Equal(0, rig.Simulator.Outputs);
        Assert.Empty(await rig.EventsAsync());
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_LogAChangeAtTheBoard_AgainstTheRelayOnThatChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);

        await rig.Simulator.SwitchRelayAsync(4, turnOn: true);
        await rig.Simulator.SetInputAsync(true);

        await rig.WaitForEventsAsync(events => events.Count == 2);
        var events = await rig.EventsAsync();
        var on = Assert.Single(events, e => e.Action == EventActions.On);
        Assert.Equal((EventActors.External, (int?)rig.RelayId(4)), (on.Actor, on.RelayId));
        var input = Assert.Single(events, e => e.Action == EventActions.InputChanged);
        Assert.Equal((int?)rig.InputId(1), input.InputId);
        var runtime = await RuntimeAsync(rig, rig.RelayId(4));
        Assert.Equal((EventActors.External, (DateTime?)BoardTestRig.Start.AddMinutes(Relay.DefaultMaxOnMinutes)), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));
    }

    // The relay on at start with nobody's run gets its own auto-off: the one on that channel, not the one with that id.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_GiveTheRelayOnAtStart_ItsOwnAutoOff(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(s => s.SetOutputsAsync(Vm201Channel.Mask(7)), layout: layout);

        await BoardTestRig.WaitUntilAsync(() => RuntimeAsync(rig, rig.RelayId(7)).GetAwaiter().GetResult().TurnedOnBy == EventActors.External);

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal([rig.RelayId(7)], await db.RelayRuntimes.Where(r => r.OnSinceUtc != null).Select(r => r.RelayId).ToListAsync(Cancel));
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_TurnOffTheRelaysChannel_WhenItsAutoOffIsDue(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await rig.Control.TurnOnAsync(rig.RelayId(3), Jane, Cancel);
        await rig.Control.TurnOnAsync(rig.RelayId(5), Jane, Cancel);
        await WaitForChannelsAsync(rig, 3, 5);

        // Relay 5's run is younger: only relay 3 is due. Relay 6 is off with a run left on it: that run ends.
        await SetRunAsync(rig, rig.RelayId(5), since: BoardTestRig.Start.AddMinutes(10), autoOffAt: BoardTestRig.Start.AddMinutes(130));
        await SetRunAsync(rig, rig.RelayId(6), since: BoardTestRig.Start, autoOffAt: null);
        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        var turnedOff = await rig.Database.Services.GetRequiredService<AutoOffService>().CheckAsync(Cancel);

        Assert.Equal([rig.RelayId(3)], turnedOff);
        await WaitForChannelsAsync(rig, 5);
        var off = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.Off);
        Assert.Equal((EventActors.AutoOff, (int?)rig.RelayId(3)), (off.Actor, off.RelayId));
        Assert.Equal(Jane, (await RuntimeAsync(rig, rig.RelayId(5))).TurnedOnBy);
        Assert.Null((await RuntimeAsync(rig, rig.RelayId(6))).OnSinceUtc);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_RunASchedule_OnTheRelaysChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await AddScheduleAsync(rig, rig.RelayId(2), 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);

        await TickAtAsync(rig, 14, 5);
        await WaitForChannelsAsync(rig, 2);
        Assert.Equal(EventActors.Schedule, (await RuntimeAsync(rig, rig.RelayId(2))).TurnedOnBy);

        await TickAtAsync(rig, 14, 10);
        await WaitForChannelsAsync(rig);
        await rig.WaitForEventsAsync(events => events.Count == 2);
        var events = await rig.EventsAsync();
        Assert.Equal([(EventActions.On, rig.RelayId(2)), (EventActions.Off, rig.RelayId(2))], events.Select(e => (e.Action, e.RelayId ?? 0)));
        Assert.All(events, e => Assert.Equal(EventActors.Schedule, e.Actor));
    }

    // At start a running window turns its relay on. After a lost connection only the relay the schedule turned on goes
    // off at its missed OffTime; one a person turned on stays on.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_ReconcileTheSchedules_ByChannel_AtStartAndReconnect(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await AddScheduleAsync(rig, rig.RelayId(3), 13, 0, 14, 10);

        await rig.Scheduler.TickAsync(Cancel);
        await WaitForChannelsAsync(rig, 3);
        await rig.Control.TurnOnAsync(rig.RelayId(4), Jane, Cancel);
        await WaitForChannelsAsync(rig, 3, 4);

        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        rig.Time.Now = new DateTimeOffset(2026, 11, 4, 13, 20, 0, TimeSpan.Zero);
        await rig.Scheduler.TickAsync(Cancel);
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        await rig.Scheduler.TickAsync(Cancel);

        await WaitForChannelsAsync(rig, 4);
        var off = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.Off);
        Assert.Equal((EventActors.Schedule, (int?)rig.RelayId(3)), (off.Actor, off.RelayId));
        Assert.Equal(Jane, (await RuntimeAsync(rig, rig.RelayId(4))).TurnedOnBy);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_StartAGroupInOrder_AndStopItWithItsRunOn_ByChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var id = await CreateGroupAsync(rig);

        await rig.Groups.StartGroupAsync(id, Jane, Cancel);
        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(Vm201Channel.Mask(1), rig.Simulator.Outputs);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig, 1, 2);

        await rig.Groups.StopGroupAsync(id, Jane, Cancel);
        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromMinutes(5);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig);

        var events = await rig.EventsAsync();
        Assert.Equal((int?)rig.RelayId(1), Assert.Single(events, e => e.Action == EventActions.RunOnStart).RelayId);
        Assert.DoesNotContain(events, e => e.Actor == EventActors.External);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_TripTheInterlock_ByChannel(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var id = await CreateGroupAsync(rig);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(2));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        await rig.Groups.StepAsync(Cancel);

        await WaitForChannelsAsync(rig, 1);
        var trip = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
        Assert.Equal((id, EventActors.System, (int?)rig.RelayId(1)), (trip.GroupId!.Value, trip.Actor, trip.RelayId));
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromMinutes(5);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig);
    }

    // "All off": the single relay at once, the group with its run-on; the group's relays aren't switched on their own.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_StopGroupsWithRunOn_AndSingleRelaysAtOnce_OnAllOff(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await CreateGroupAsync(rig);
        await rig.Simulator.SetOutputsAsync(0b0000_0111);
        await WaitForChannelsAsync(rig, 1, 2, 3);

        await rig.Control.TurnAllOffAsync(rig.Live.BoardId, Jane, Cancel);

        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        var events = await rig.EventsAsync();
        Assert.Contains(events, e => e.Action == EventActions.Off && e.RelayId == rig.RelayId(3) && e.Actor == Jane);
        Assert.Contains(events, e => e.Action == EventActions.RunOnStart && e.RelayId == rig.RelayId(1));
        Assert.DoesNotContain(events, e => e.Action == EventActions.Off && e.Actor == Jane && e.RelayId != rig.RelayId(3));
        await Assert.ThrowsAsync<RelayInGroupException>(() => rig.Control.TurnOnAsync(rig.RelayId(2), Jane, Cancel));
    }

    // A group only starts with its relays off, as the board shows them by channel.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_RefuseAGroup_WhileOneOfItsRelaysIsOn(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(2));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        var (id, error) = await rig.GroupSetup.CreateAsync(FanHeater(rig), Jane, Cancel);

        Assert.Equal((null, "Turn off Relay 2 first."), (id, error));
        var candidates = await rig.GroupSetup.CandidatesAsync(Cancel);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], candidates.Select(c => c.Channel));
        Assert.Equal(Enumerable.Range(1, 8).Select(rig.RelayId), candidates.Select(c => c.RelayId));
    }

    // A group's schedule counts the group as on by its members' channels: it starts it in order at the OnTime and stops
    // it with its run-on at the OffTime.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_RunAGroupsSchedule_ByItsChannels(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var groupId = await CreateGroupAsync(rig);
        await AddGroupScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);

        await TickAtAsync(rig, 14, 5);
        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig, 1, 2);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(EventActors.Schedule, (await GroupRuntimeAsync(rig, groupId)).TurnedOnBy);

        await TickAtAsync(rig, 14, 10);
        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now = new DateTimeOffset(2026, 11, 4, 13, 15, 1, TimeSpan.Zero);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig);

        var events = (await rig.EventsAsync()).Where(e => e.GroupId == groupId).ToList();
        Assert.Contains(events, e => e is { Action: EventActions.GroupOn, Actor: EventActors.Schedule });
        Assert.Contains(events, e => e is { Action: EventActions.GroupOff, Actor: EventActors.Schedule });
        Assert.Equal((int?)rig.RelayId(1), Assert.Single(events, e => e.Action == EventActions.RunOnStart).RelayId);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
    }

    // The fan switched on at the board just before the window: once the window runs, the group is the schedule's.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_LetAGroupsSchedule_TakeOverTheGroupOnOutsideTheApp(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var groupId = await CreateGroupAsync(rig);
        await AddGroupScheduleAsync(rig, groupId, 14, 5, 18, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 4);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(1));
        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(EventActors.External, (await GroupRuntimeAsync(rig, groupId)).TurnedOnBy);

        await TickAtAsync(rig, 14, 5);

        var runtime = await GroupRuntimeAsync(rig, groupId);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));

        // Taken over as it is, the fan alone, as a person left it: not started again.
        Assert.Equal(GroupPhases.Idle, runtime.Phase);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.GroupOn);
        await rig.Groups.StepAsync(Cancel);
        await WaitForChannelsAsync(rig, 1);
    }

    // The screens' view of a group: its members' channels, the chain they show, and on or off by the board's channels.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_ShowAGroupsMembers_ByTheirChannels(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var groupId = await CreateGroupAsync(rig);
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(1));
        await WaitForChannelsAsync(rig, 1);

        var group = await rig.GroupSetup.GetAsync(groupId, Cancel);
        var listed = Assert.Single(await rig.GroupSetup.ListAsync(Cancel));

        foreach (var view in new[] { group!, listed })
        {
            Assert.Equal([(rig.RelayId(1), 1), (rig.RelayId(2), 2)], view.Members.Select(m => (m.RelayId, m.Channel)));
            Assert.Equal("RELAY 1 → 2", view.Chain);
            Assert.Equal(ChannelMask.Of([1, 2]), view.Mask);
            Assert.Null(view.MissingRelay(rig.Simulator.Model.Capabilities.RelayCount));
            Assert.True(view.IsOn(Board(rig).State));
            Assert.Equal(1, GroupPlanner.Level(view.Infos, Board(rig).State));
        }
    }

    // A trip had sent the fan's ON for its run-on, lost with the connection: after the reconnect it goes once more, to
    // the fan's channel.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_SendTheFanOnceMore_WhenATripsOnWasLostInAReconnect(RelayLayout layout)
    {
        await using var rig = await BoardTestRig.StartAsync(layout: layout);
        var groupId = await CreateGroupAsync(rig);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.GroupRuntimes.SingleAsync(r => r.GroupId == groupId, Cancel);
            (runtime.Phase, runtime.TargetLevel, runtime.PendingPosition, runtime.PendingOn, runtime.PendingSinceUtc) =
                (GroupPhases.Stopping, 0, 1, true, BoardTestRig.Start);
            await db.SaveChangesAsync(Cancel);
        }

        _ = rig.Groups;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromMinutes(1);
        await rig.Groups.StepAsync(Cancel);

        await WaitForChannelsAsync(rig, 1);
        await rig.Groups.StepAsync(Cancel);
        Assert.Contains(await rig.EventsAsync(), e => e.Action == EventActions.RunOnStart && e.RelayId == rig.RelayId(1));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.GroupStartFailed);
    }

    // At start each input is compared with what the log last says about that input, found by its channel: one that
    // changed while the app was down is logged as noticed; one that reads the same logs nothing.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_LogAnInputThatChangedWhileDown_AsNoticed(RelayLayout layout)
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 2);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSingleton<InputChangeFeed>();
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 0, inputCount: 0, Cancel);
        await RelayLayouts.SeedAsync(database.Services, layout, relayCount: 4, inputCount: 2);
        await database.Services.SeedBoardAsync(relayCount: 4, inputCount: 2, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        int Input(int channel) => RelayLayouts.InputId(layout, channel, 2);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            (await db.BoardRuntimes.SingleAsync(Cancel)).LastContactUtc = BoardTestRig.Start.AddMinutes(-2);
            db.EventLog.Add(new EventLogEntry { TimestampUtc = BoardTestRig.Start.AddHours(-1), InputId = Input(1), Actor = EventActors.External, Action = EventActions.InputChanged, Details = """{"on":false}""" });
            db.EventLog.Add(new EventLogEntry { TimestampUtc = BoardTestRig.Start.AddHours(-1), InputId = Input(2), Actor = EventActors.External, Action = EventActions.InputChanged, Details = """{"on":true}""" });
            await db.SaveChangesAsync(Cancel);
        }

        board.SetInput(1, on: true);
        board.SetInput(2, on: true);
        var recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            await BoardTestRig.WaitUntilAsync(() => Events(database).Count(e => e.Action == EventActions.InputChanged) == 3);
            await Task.Delay(100, Cancel);

            var changes = Events(database).Where(e => e.Action == EventActions.InputChanged).ToList();
            Assert.Equal(3, changes.Count);
            Assert.Equal(((int?)Input(1), """{"on":true,"noticed":true}"""), (changes[2].InputId, changes[2].Details));
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    // From the board's input on its channel, through the recorder and the input's actions, to the relay on its channel.
    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_SwitchAnInputsRelays_ByChannel(RelayLayout layout)
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 2);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddInputs(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 0, inputCount: 0, Cancel);
        await RelayLayouts.SeedAsync(database.Services, layout, relayCount: 4, inputCount: 2);
        await database.Services.SeedBoardAsync(relayCount: 4, inputCount: 2, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        int Relay(int channel) => RelayLayouts.RelayId(layout, channel, 4);
        var input = RelayLayouts.InputId(layout, 2, 2);
        var (groupId, error) = await database.Services.GetRequiredService<GroupsHandler>().CreateAsync(
            new GroupDraft("Heater", GroupIcons.Fan, 120, [new GroupMemberDraft(Relay(1), "Fan"), new GroupMemberDraft(Relay(2), "Heat")]),
            Jane,
            Cancel);
        Assert.Null(error);
        var handler = database.Services.GetRequiredService<InputActionsHandler>();
        var targets = await handler.TargetsAsync(input, Cancel);
        Assert.Equal(["Group: Heater", "Relay 3: Relay 3", "Relay 4: Relay 4"], targets.Select(t => t.Label));
        Assert.Null(await handler.SaveAsync(
            input,
            [
                new InputActionRow(targets.Single(t => t.GroupId == groupId), WhenClosed: true, WhenOpened: false),
                new InputActionRow(targets.Single(t => t.RelayId == Relay(3)), WhenClosed: true, WhenOpened: false),
            ],
            Cancel));
        var hosted = database.Services.GetServices<IHostedService>().ToList();
        var recorder = hosted.OfType<BoardEventRecorder>().Single();
        var feed = database.Services.GetRequiredService<InputChangeFeed>();
        await recorder.StartAsync(Cancel);
        try
        {
            board.SetInput(2, on: true);
            await BoardTestRig.WaitUntilAsync(() => Events(database).Any(e => e.Action == EventActions.InputChanged));

            Assert.True(feed.Reader.TryRead(out var change));
            Assert.Equal(new InputChanged(input, Closed: true), change);
            await hosted.OfType<InputActionService>().Single().RunAsync(change, Cancel);

            // The group's first relay and relay 3, each on its channel; the group's second waits for its start delay.
            Assert.Equal(ChannelMask.Of([1, 3]), board.State.Relays);
            var events = Events(database);
            var changed = Assert.Single(events, e => e.Action == EventActions.InputChanged);
            Assert.Equal((int?)input, changed.InputId);
            var on = Assert.Single(events, e => e.Action == EventActions.On);
            Assert.Equal((EventActors.ForInput(input), (int?)Relay(3)), (on.Actor, on.RelayId));
            Assert.Contains(events, e => e.Action == EventActions.GroupOn && e.GroupId == groupId && e.Actor == EventActors.ForInput(input));

            // Already on: left alone, nothing logged twice.
            await hosted.OfType<InputActionService>().Single().RunAsync(change, Cancel);
            Assert.Single(Events(database), e => e.Action == EventActions.On);
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_ShowEachRelayAndInput_ByItsChannel_AndLinkToIt_ByItsId(RelayLayout layout)
    {
        var board = new FakeRelayBoard(relayCount: 8, inputCount: 2);
        board.SetRelay(3, on: true);
        board.SetInput(2, on: true);
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithBoard(board));
        app.UseLayoutBeforeStart(layout, relayCount: 8, inputCount: 2);
        int Relay(int channel) => RelayLayouts.RelayId(layout, channel, 8);
        int Input(int channel) => RelayLayouts.InputId(layout, channel, 2);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        var relays = RelayCard().Matches(html).Select(m => (On: m.Groups["on"].Success, Id: int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture), Label: m.Groups["label"].Value)).ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(c => (c == 3, Relay(c), $"RELAY {c}")), relays);
        var inputs = InputCard().Matches(html).Select(m => (Id: int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture), Label: m.Groups["label"].Value)).ToList();
        Assert.Equal([(Input(1), "INPUT 1"), (Input(2), "INPUT 2")], inputs);
        Assert.Contains(">1 of 8 on</strong>", html, StringComparison.Ordinal);

        var relay = WebUtility.HtmlDecode(await http.GetStringAsync($"/relay/{Relay(3)}", Cancel));
        Assert.Contains("RELAY 3 · NAME", relay, StringComparison.Ordinal);
        Assert.Contains("TURN OFF", relay, StringComparison.Ordinal);
        var off = WebUtility.HtmlDecode(await http.GetStringAsync($"/relay/{Relay(4)}", Cancel));
        Assert.Contains("RELAY 4 · NAME", off, StringComparison.Ordinal);
        Assert.Contains("TURN ON", off, StringComparison.Ordinal);
        var input = WebUtility.HtmlDecode(await http.GetStringAsync($"/input/{Input(2)}", Cancel));
        Assert.Contains("INPUT 2 · NAME", input, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RelayLayout.Same)]
    [InlineData(RelayLayout.Apart)]
    public async Task Should_ReadEachRelaysAndInputsChannel(RelayLayout layout)
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 0, inputCount: 0, Cancel);
        await RelayLayouts.SeedAsync(database.Services, layout, relayCount: 4, inputCount: 2);
        await database.Services.SeedBoardAsync(relayCount: 4, inputCount: 2, Cancel);

        var channels = await BoardChannels.LoadAsync(database.Factory, await database.Services.FirstBoardIdAsync(), Cancel);

        for (var channel = 1; channel <= 4; channel++)
        {
            var id = RelayLayouts.RelayId(layout, channel, 4);
            Assert.Equal((channel, id), (channels.RelayChannel(id), channels.RelayAt(channel)));
        }

        Assert.Equal((2, RelayLayouts.InputId(layout, 2, 2)), (channels.InputChannel(RelayLayouts.InputId(layout, 2, 2)), channels.InputAt(2)));
        Assert.Equal([RelayLayouts.RelayId(layout, 1, 4), RelayLayouts.RelayId(layout, 4, 4)], channels.RelayIds(ChannelMask.Of([1, 4, 5])));
        Assert.Null(channels.RelayAt(5));
        Assert.Null(channels.RelayChannel(999));
    }

    // A group's chain, its mask and a relay the board doesn't have go by the channels; links by the ids.
    [Fact]
    public void Should_ShowAGroupsRelays_ByTheirChannels()
    {
        var group = new GroupView(
            1,
            "Fan heater",
            GroupIcons.Fan,
            120,
            [new GroupMemberView(108, 1, 1, "Fan", 3, 300), new GroupMemberView(107, 2, 2, "Heat", 3, 0), new GroupMemberView(42, 9, 3, "Mist", 3, 0)],
            new GroupRuntime { GroupId = 1 });

        Assert.Equal("RELAY 1 → 2 → 9", group.Chain);
        Assert.Equal(ChannelMask.Of([1, 2, 9]), group.Mask);
        Assert.Equal(9, group.MissingRelay(relayCount: 8));
        Assert.Null(group.MissingRelay(relayCount: 9));
        Assert.True(group.IsOn(new BoardState(true, ChannelMask.Of(9), ChannelMask.None, null)));
        Assert.False(group.IsOn(new BoardState(true, ChannelMask.Of(42 % 64), ChannelMask.None, null)));
    }

    // Until the simulated board has exactly these channels on, and the app has seen it.
    private static Task WaitForChannelsAsync(BoardTestRig rig, params int[] channels)
    {
        var outputs = Outputs(channels);
        return BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == outputs && rig.Client.State.Outputs == outputs);
    }

    // The VM201's outputs with these channels on.
    private static byte Outputs(params int[] channels)
    {
        return (byte)ChannelMask.Of(channels).Bits;
    }

    private static List<EventLogEntry> Events(TestDatabase database)
    {
        using var db = database.Factory.CreateDbContext();
        return [.. db.EventLog.AsNoTracking().OrderBy(e => e.Id)];
    }

    private static async Task<int> CreateGroupAsync(BoardTestRig rig)
    {
        var (id, error) = await rig.GroupSetup.CreateAsync(FanHeater(rig), Jane, Cancel);
        Assert.Null(error);
        return id!.Value;
    }

    private static async Task<RelayRuntime> RuntimeAsync(BoardTestRig rig, int relayId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    private static async Task SetRunAsync(BoardTestRig rig, int relayId, DateTime since, DateTime? autoOffAt)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var run = await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, Cancel);
        (run.OnSinceUtc, run.AutoOffAtUtc, run.TurnedOnBy) = (since, autoOffAt, Jane);
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task AddScheduleAsync(BoardTestRig rig, int relayId, int onHour, int onMinute, int offHour, int offMinute)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(onHour, onMinute),
            OffTime = new TimeOnly(offHour, offMinute),
            IsEnabled = true,
            CreatedBy = Jane,
        });
        await db.SaveChangesAsync(Cancel);
    }

    // The board as the app sees it.
    private static LiveBoard Board(BoardTestRig rig)
    {
        return rig.Database.Services.Live();
    }

    private static async Task AddGroupScheduleAsync(BoardTestRig rig, int groupId, int onHour, int onMinute, int offHour, int offMinute)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            GroupId = groupId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(onHour, onMinute),
            OffTime = new TimeOnly(offHour, offMinute),
            IsEnabled = true,
            CreatedBy = Jane,
        });
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task<GroupRuntime> GroupRuntimeAsync(BoardTestRig rig, int groupId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.GroupRuntimes.AsNoTracking().SingleAsync(r => r.GroupId == groupId, Cancel);
    }

    // 14:00 local is 13:00 UTC on the rig's day.
    private static async Task TickAtAsync(BoardTestRig rig, int hour, int minute)
    {
        rig.Time.Now = new DateTimeOffset(2026, 11, 4, hour - 1, minute, 0, TimeSpan.Zero);
        await rig.Scheduler.TickAsync(Cancel);
    }

    [GeneratedRegex("""<div class="card (?<on>on)? [^"]*"[^>]*>\s*<a class="text" href="relay/(?<id>\d+)"[^>]*>\s*<span class="label"[^>]*>(?<label>RELAY \d+)</span>""")]
    private static partial Regex RelayCard();

    [GeneratedRegex("""<a class="title" href="input/(?<id>\d+)"[^>]*>\s*<span class="label"[^>]*>(?<label>INPUT \d+)</span>""")]
    private static partial Regex InputCard();
}
