using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The rules of a relay group, without a board: start order, stop order with run-on, the interlock and one relay at a
/// time. The board's status is given by hand, and time moves only when a test moves it.
/// </summary>
public sealed class GroupPlannerTests
{
    private const string John = "john@example.com";
    private static readonly DateTime T0 = new(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc);

    // The fan heater: the fan (relay 1) first, the heat (relay 2) three seconds later; the fan runs on 5 minutes.
    private static readonly IReadOnlyList<GroupMemberInfo> FanHeater =
    [
        new(1, 1, "Fan", 3, 300),
        new(2, 2, "Heat", 3, 0),
    ];

    // Three relays with different waits: ventilation, compressor, compressed air.
    private static readonly IReadOnlyList<GroupMemberInfo> Spray =
    [
        new(1, 5, "Ventilation", 3, 120),
        new(2, 6, "Compressor", 10, 60),
        new(3, 7, "Tryckluft", 2, 0),
    ];

    private readonly GroupRuntime _runtime = new() { GroupId = 1 };
    private IReadOnlyList<GroupMemberInfo> _members = FanHeater;

    [Fact]
    public void Should_StartFromTheTop_EachStepAfterConfirmationAndItsDelay()
    {
        var commands = new GroupCommands();
        Assert.True(GroupPlanner.Start(FanHeater, State(), _runtime, John, 120, T0, commands));
        Assert.Equal((Mask(1), ChannelMask.None), (commands.On, commands.Off));
        Assert.Equal(EventActions.GroupOn, Assert.Single(commands.Events).Action);

        // Not confirmed yet: nothing more.
        Assert.True(Plan(State(), T0.AddSeconds(1)).IsEmpty);

        // Confirmed at 1 s; the heat may start 3 s later.
        Assert.True(Plan(State(1), T0.AddSeconds(1)).IsEmpty);
        Assert.Equal(T0.AddSeconds(4), _runtime.NextStepAtUtc);
        Assert.True(Plan(State(1), T0.AddSeconds(3.9)).IsEmpty);
        Assert.Equal(Mask(2), Plan(State(1), T0.AddSeconds(4)).On);

        Plan(State(1, 2), T0.AddSeconds(4.5));
        Plan(State(1, 2), T0.AddSeconds(5));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.Equal((T0, John, T0.AddMinutes(120)), (_runtime.OnSinceUtc!.Value, _runtime.TurnedOnBy, _runtime.AutoOffAtUtc!.Value));
    }

    // A group on with nobody's run (its fan switched at the board, say) gets the group's own auto-off from then; one
    // under way, or already someone's, is left alone.
    [Fact]
    public void Should_GiveAnOwnerlessGroup_ItsOwnAutoOff()
    {
        GroupPlanner.GiveOwnerlessAutoOff(FanHeater, State(), _runtime, 120, T0);
        Assert.Null(_runtime.OnSinceUtc);

        GroupPlanner.GiveOwnerlessAutoOff(FanHeater, State(1), _runtime, 120, T0);
        Assert.Equal((T0, EventActors.External, T0.AddMinutes(120)), (_runtime.OnSinceUtc!.Value, _runtime.TurnedOnBy, _runtime.AutoOffAtUtc!.Value));

        // Someone's run stays theirs.
        GroupPlanner.GiveOwnerlessAutoOff(FanHeater, State(1), _runtime, 120, T0.AddMinutes(5));
        Assert.Equal(T0, _runtime.OnSinceUtc);

        // A sequence under way is left to it.
        var starting = new GroupRuntime { GroupId = 2, Phase = GroupPhases.Starting };
        GroupPlanner.GiveOwnerlessAutoOff(FanHeater, State(1), starting, 120, T0);
        Assert.Null(starting.OnSinceUtc);

        // "Never" gives no auto-off, but it is still on outside the app.
        var never = new GroupRuntime { GroupId = 3 };
        GroupPlanner.GiveOwnerlessAutoOff(FanHeater, State(1), never, null, T0);
        Assert.Equal((EventActors.External, (DateTime?)null), (never.TurnedOnBy, never.AutoOffAtUtc));
    }

    [Fact]
    public void Should_StopStarting_WhenTheBoardNeverConfirms()
    {
        GroupPlanner.Start(FanHeater, State(), _runtime, John, null, T0, new GroupCommands());

        Assert.True(Plan(State(), T0.AddSeconds(4.9)).IsEmpty);
        var failed = Plan(State(), T0.AddSeconds(5));

        Assert.Equal(ChannelMask.None, failed.On);
        Assert.Equal(EventActions.GroupStartFailed, Assert.Single(failed.Events).Action);
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.True(Plan(State(), T0.AddSeconds(30)).IsEmpty);
    }

    [Fact]
    public void Should_KeepWhatRuns_WhenALaterStepIsNeverConfirmed()
    {
        GroupPlanner.Start(FanHeater, State(), _runtime, John, null, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));
        Plan(State(1), T0.AddSeconds(4));

        var failed = Plan(State(1), T0.AddSeconds(9));

        Assert.Equal((ChannelMask.None, ChannelMask.None), (failed.On, failed.Off));
        Assert.Equal((EventActions.GroupStartFailed, (int?)2), (failed.Events[0].Action, failed.Events[0].RelayId));
    }

    [Fact]
    public void Should_StopFromTheBottom_WithRunOn()
    {
        var stop = new GroupCommands();
        Assert.True(GroupPlanner.Stop(FanHeater, State(1, 2), _runtime, John, T0, stop));
        Assert.Equal(Mask(2), stop.Off);
        Assert.Equal(EventActions.GroupOff, stop.Events[0].Action);

        // The heat is off: the fan runs on 5 minutes.
        var runOn = Plan(State(1), T0.AddSeconds(1));
        Assert.Equal((EventActions.RunOnStart, (int?)1), (runOn.Events[0].Action, runOn.Events[0].RelayId));
        Assert.Equal("Fan keeps running for 5 min", EventText.For(Logged(runOn.Events[0]), null, null, "Fan heater").What);
        Assert.True(Plan(State(1), T0.AddSeconds(300)).IsEmpty);

        var fanOff = Plan(State(1), T0.AddSeconds(301));
        Assert.Equal(Mask(1), fanOff.Off);
        var ended = Assert.Single(fanOff.Events);
        Assert.Equal("Fan off", EventText.For(Logged(ended), null, null, "Fan heater").What);

        Plan(State(), T0.AddSeconds(302));
        Plan(State(), T0.AddSeconds(303));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.Null(_runtime.OnSinceUtc);
    }

    [Fact]
    public void Should_CarryOn_WhenStartedDuringRunOn()
    {
        GroupPlanner.Stop(FanHeater, State(1, 2), _runtime, John, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));

        var start = new GroupCommands();
        GroupPlanner.Start(FanHeater, State(1), _runtime, John, null, T0.AddSeconds(60), start);

        Assert.Equal((ChannelMask.None, Mask(2)), (start.Off, start.On));
    }

    [Fact]
    public void Should_RunThreeRelays_WithTheirOwnWaits()
    {
        _members = Spray;
        GroupPlanner.Start(Spray, State(), _runtime, John, null, T0, new GroupCommands());
        Plan(State(5), T0);
        Assert.Equal(T0.AddSeconds(10), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(6), Plan(State(5), T0.AddSeconds(10)).On);
        Plan(State(5, 6), T0.AddSeconds(10));
        Assert.Equal(T0.AddSeconds(12), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(7), Plan(State(5, 6), T0.AddSeconds(12)).On);
        Plan(State(5, 6, 7), T0.AddSeconds(12));

        GroupPlanner.Stop(Spray, State(5, 6, 7), _runtime, John, T0.AddSeconds(100), new GroupCommands());
        Plan(State(5, 6), T0.AddSeconds(100));
        Assert.Equal(T0.AddSeconds(160), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(6), Plan(State(5, 6), T0.AddSeconds(160)).Off);
        Plan(State(5), T0.AddSeconds(160));
        Assert.Equal(T0.AddSeconds(280), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(5), Plan(State(5), T0.AddSeconds(280)).Off);
    }

    [Fact]
    public void Should_TripTheInterlock_WhenTheHeatRunsWithoutTheFan()
    {
        // Turned on at the board itself.
        var trip = Plan(State(2), T0);

        Assert.Equal((Mask(2), Mask(1)), (trip.Off, trip.On));
        var e = Assert.Single(trip.Events);
        Assert.Equal((EventActions.InterlockTrip, EventActors.System), (e.Action, e.Actor));
        using (var details = System.Text.Json.JsonDocument.Parse(e.Details!))
        {
            Assert.Equal("Fan", details.RootElement.GetProperty("missing").GetString());
            Assert.Equal(["Heat"], details.RootElement.GetProperty("off").EnumerateArray().Select(x => x.GetString()));
        }

        // The same old status again before the board reports back: nothing new.
        Assert.True(Plan(State(2), T0.AddSeconds(1)).IsEmpty);

        // The fan runs on, then stops.
        Assert.Equal(EventActions.RunOnStart, Plan(State(1), T0.AddSeconds(2)).Events[0].Action);
        Assert.Equal(Mask(1), Plan(State(1), T0.AddSeconds(302)).Off);
    }

    [Fact]
    public void Should_TripTheInterlock_WhenTheFanStopsUnderTheHeat()
    {
        _runtime.OnSinceUtc = T0;

        var trip = Plan(State(2), T0.AddMinutes(10));

        Assert.Equal((Mask(2), Mask(1)), (trip.Off, trip.On));
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);
    }

    [Fact]
    public void Should_TurnOffEverythingBelowTheGap()
    {
        _members = Spray;
        var trip = Plan(State(5, 7), T0);

        Assert.Equal((Mask(7), Mask(6)), (trip.Off, trip.On));
    }

    [Fact]
    public void Should_RefuseARelay_WhoseRelayAboveIsOff()
    {
        var commands = new GroupCommands();

        var refusal = GroupPlanner.MemberOn(FanHeater, State(), _runtime, 2, John, null, T0, commands);

        Assert.Equal("Needs Fan to be on.", refusal);
        Assert.True(commands.IsEmpty);
    }

    [Fact]
    public void Should_RunTheFanAlone()
    {
        var commands = new GroupCommands();

        Assert.Null(GroupPlanner.MemberOn(FanHeater, State(), _runtime, 1, John, 60, T0, commands));

        Assert.Equal(Mask(1), commands.On);
        Assert.Equal((EventActions.On, (int?)1), (commands.Events[0].Action, commands.Events[0].RelayId));
        Assert.Equal(T0.AddMinutes(60), _runtime.AutoOffAtUtc);
    }

    [Fact]
    public void Should_StopTheRelaysBelow_ThenRunOn_WhenTheFanIsTurnedOff()
    {
        var off = new GroupCommands();
        GroupPlanner.MemberOff(FanHeater, State(1, 2), _runtime, 1, John, T0, off);

        Assert.Equal(Mask(2), off.Off);
        Assert.Equal(EventActions.RunOnStart, Plan(State(1), T0.AddSeconds(1)).Events[0].Action);
        Assert.Equal(Mask(1), Plan(State(1), T0.AddSeconds(301)).Off);
    }

    [Fact]
    public void Should_TurnOffAtOnce_TheLowestRelay()
    {
        var off = new GroupCommands();

        GroupPlanner.MemberOff(FanHeater, State(1, 2), _runtime, 2, John, T0, off);

        Assert.Equal(Mask(2), off.Off);
        Plan(State(1), T0.AddSeconds(1));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.True(Plan(State(1), T0.AddMinutes(10)).IsEmpty);
    }

    [Fact]
    public void Should_StopTheGroup_WhenItsAutoOffTimeHasPassed()
    {
        GroupPlanner.Start(FanHeater, State(), _runtime, John, 60, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));
        Plan(State(1), T0.AddSeconds(4));
        Plan(State(1, 2), T0.AddSeconds(5));
        Plan(State(1, 2), T0.AddSeconds(6));

        var autoOff = Plan(State(1, 2), T0.AddMinutes(60));

        Assert.Equal(Mask(2), autoOff.Off);
        Assert.Equal((EventActions.GroupOff, EventActors.AutoOff), (autoOff.Events[0].Action, autoOff.Events[0].Actor));
        Assert.Contains("\"afterMinutes\":60", autoOff.Events[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CarryOnAfterARestart_FromTheSavedRuntime()
    {
        // As saved during a run-on, then read back by a new process 2 minutes after the run-on ended.
        var saved = new GroupRuntime
        {
            GroupId = 1,
            Phase = GroupPhases.Stopping,
            TargetLevel = 0,
            NextStepAtUtc = T0.AddMinutes(5),
            OnSinceUtc = T0.AddMinutes(-30),
            TurnedOnBy = John,
        };
        var commands = new GroupCommands();

        GroupPlanner.Plan(FanHeater, State(1), saved, T0.AddMinutes(7), commands);

        Assert.Equal(Mask(1), commands.Off);
    }

    [Fact]
    public void Should_SendTheStopAgain_WhenTheBoardDoesNotConfirmIt()
    {
        GroupPlanner.Stop(FanHeater, State(1, 2), _runtime, John, T0, new GroupCommands());

        Assert.Equal(Mask(2), Plan(State(1, 2), T0.AddSeconds(5)).Off);
    }

    [Fact]
    public void Should_RunOn_WhenAStopOvertakesTheHeatOnItsWay()
    {
        GroupPlanner.Start(FanHeater, State(), _runtime, John, null, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));
        Assert.Equal(Mask(2), Plan(State(1), T0.AddSeconds(4)).On);

        // Stop before the board reports the heat on: the heat gets an OFF, the fan stays.
        var stop = new GroupCommands();
        Assert.True(GroupPlanner.Stop(FanHeater, State(1), _runtime, John, T0.AddSeconds(4.5), stop));
        Assert.Equal(Mask(2), stop.Off);

        // The board reports the heat on, then off; the fan runs on the full 5 minutes from there.
        Assert.Equal(ChannelMask.None, Plan(State(1, 2), T0.AddSeconds(4.6)).Off & Mask(1));
        Assert.Equal(EventActions.RunOnStart, Plan(State(1), T0.AddSeconds(4.7)).Events[0].Action);
        Assert.Equal(ChannelMask.None, Plan(State(1), T0.AddSeconds(300)).Off);
        Assert.Equal(Mask(1), Plan(State(1), T0.AddSeconds(305)).Off);
    }

    [Fact]
    public void Should_StopTheFan_WhenStoppedRightAfterItWasTurnedOn()
    {
        GroupPlanner.MemberOn(FanHeater, State(), _runtime, 1, John, null, T0, new GroupCommands());
        var stop = new GroupCommands();

        Assert.True(GroupPlanner.Stop(FanHeater, State(), _runtime, John, T0.AddSeconds(0.5), stop));

        Assert.Equal(Mask(1), stop.Off);
    }

    [Fact]
    public void Should_GiveUp_WhenTheFanATripStartedNeverComesOn()
    {
        Plan(State(2), T0);

        // The heat went off, the fan never came on.
        var failed = Plan(State(), T0.AddSeconds(5));
        Assert.Equal(ChannelMask.None, failed.On);
        Assert.Equal((EventActions.GroupStartFailed, (int?)1), (failed.Events[0].Action, failed.Events[0].RelayId));
        for (var second = 6; second < 60; second++)
        {
            Assert.True(Plan(State(), T0.AddSeconds(second)).IsEmpty);
        }

        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_RunOnTheRelayAbove_WhenTheMissingRelayNeverComesOn()
    {
        _members = Spray;
        Plan(State(5, 7), T0);

        // The compressed air went off, the compressor never came on: the ventilation still runs on its 2 minutes.
        Plan(State(5), T0.AddSeconds(5));
        Assert.True(Plan(State(5), T0.AddSeconds(124)).IsEmpty);
        Assert.Equal(Mask(5), Plan(State(5), T0.AddSeconds(125)).Off);
    }

    [Fact]
    public void Should_LeaveAStartAlone_WhenTheFanIsTurnedOnAgain()
    {
        GroupPlanner.Start(FanHeater, State(), _runtime, John, null, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));

        GroupPlanner.MemberOn(FanHeater, State(1), _runtime, 1, John, null, T0.AddSeconds(2), new GroupCommands());

        Assert.Equal(Mask(2), Plan(State(1), T0.AddSeconds(4)).On);
    }

    [Fact]
    public void Should_KeepTheHeatOffAndTheFanTrying_WhileTheHeatWontGoOff()
    {
        Plan(State(2), T0);

        var again = Plan(State(2), T0.AddSeconds(5));

        Assert.Equal((Mask(2), Mask(1)), (again.Off, again.On));
        Assert.Empty(again.Events);
    }

    [Fact]
    public void Should_KeepTheFan_WhenItIsTurnedOnDuringItsRunOn()
    {
        GroupPlanner.Stop(FanHeater, State(1, 2), _runtime, John, T0, new GroupCommands());
        Plan(State(1), T0.AddSeconds(1));

        Assert.Null(GroupPlanner.MemberOn(FanHeater, State(1), _runtime, 1, John, null, T0.AddSeconds(60), new GroupCommands()));

        Assert.True(Plan(State(1), T0.AddSeconds(400)).IsEmpty);
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_TimeTheRunOn_FromAConfirmationAfterAReconnect()
    {
        // The heat's OFF went out before the connection dropped; the sequencer gives the board a fresh timeout.
        GroupPlanner.Stop(FanHeater, State(1, 2), _runtime, John, T0, new GroupCommands());
        _runtime.PendingSinceUtc = T0.AddMinutes(3);

        Assert.Equal(EventActions.RunOnStart, Plan(State(1), T0.AddMinutes(3)).Events[0].Action);
        Assert.Equal(T0.AddMinutes(8), _runtime.NextStepAtUtc);
    }

    [Fact]
    public void Should_StopNothing_WhenTheGroupIsOff()
    {
        var commands = new GroupCommands();

        Assert.False(GroupPlanner.Stop(FanHeater, State(), _runtime, John, T0, commands));
        Assert.True(commands.IsEmpty);
    }

    private GroupCommands Plan(BoardState state, DateTime now)
    {
        var commands = new GroupCommands();
        GroupPlanner.Plan(_members, state, _runtime, now, commands);
        return commands;
    }

    private static ChannelMask Mask(int relayId) => ChannelMask.Of(relayId);

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, T0);

    // As the sequencer stores it, so the run-on texts in Events read the role from it.
    private static EventLogEntry Logged(GroupEvent e) =>
        new() { GroupId = 1, RelayId = e.RelayId, Actor = e.Actor, Action = e.Action, Details = e.Details };
}
