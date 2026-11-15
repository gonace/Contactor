using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// A group whose steps hold several relays, without a board: the relays of a step switch together, the interlock goes
/// step by step, and one relay can still be switched by hand. The board's status is given by hand, and time moves only
/// when a test moves it.
/// </summary>
public sealed class GroupStepsTests
{
    private const string John = "john@example.com";
    private static readonly DateTime T0 = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);

    // Two fans (relays 1 and 2), then two heaters (relays 3 and 4) three seconds later; the fans run on 3 minutes.
    // The relays' ids aren't their channels.
    private static readonly IReadOnlyList<GroupStepInfo> FansHeaters =
    [
        new("Fans", 3, 180, [new(21, 1, "Fan 1"), new(22, 2, "Fan 2")]),
        new("Heaters", 3, 0, [new(23, 3, "Heater 1"), new(24, 4, "Heater 2")]),
    ];

    private readonly GroupRuntime _runtime = new() { GroupId = 1 };

    [Fact]
    public void Should_StartEveryRelayOfAStepTogether_ThenTheNextStepAfterItsWait()
    {
        var start = new GroupCommands();
        Assert.True(GroupPlanner.Start(FansHeaters, State(), _runtime, John, null, T0, start));
        Assert.Equal((Mask(1, 2), ChannelMask.None), (start.On, start.Off));

        // One fan on is not the step on: the heaters wait.
        Assert.True(Plan(State(1), T0.AddSeconds(1)).IsEmpty);
        Assert.Null(_runtime.NextStepAtUtc);

        Assert.True(Plan(State(1, 2), T0.AddSeconds(1)).IsEmpty);
        Assert.Equal(T0.AddSeconds(4), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(3, 4), Plan(State(1, 2), T0.AddSeconds(4)).On);

        Plan(State(1, 2, 3, 4), T0.AddSeconds(5));
        Plan(State(1, 2, 3, 4), T0.AddSeconds(6));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_StopStarting_WhenOneRelayOfTheStepNeverComesOn()
    {
        GroupPlanner.Start(FansHeaters, State(), _runtime, John, null, T0, new GroupCommands());

        var failed = Plan(State(1), T0.AddSeconds(5));

        var e = Assert.Single(failed.Events);
        Assert.Equal((EventActions.GroupStartFailed, (int?)22), (e.Action, e.RelayId));
        Assert.Equal("Fan 2", Read(e.Details, "role"));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.True(Plan(State(1), T0.AddSeconds(30)).IsEmpty);
    }

    [Fact]
    public void Should_StartOnlyTheRelaysOfTheStepThatAreOff()
    {
        // Fan 1 was turned on by hand.
        var start = new GroupCommands();
        GroupPlanner.Start(FansHeaters, State(1), _runtime, John, null, T0, start);

        Assert.Equal(Mask(2), start.On);
    }

    [Fact]
    public void Should_StopTheLastStepTogether_ThenRunOnTheStepAbove()
    {
        var stop = new GroupCommands();
        Assert.True(GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, stop));
        Assert.Equal(Mask(3, 4), stop.Off);

        // One heater off is not the step off.
        Assert.True(Plan(State(1, 2, 4), T0.AddSeconds(1)).IsEmpty);

        var runOn = Assert.Single(Plan(State(1, 2), T0.AddSeconds(1)).Events);
        Assert.Equal((EventActions.RunOnStart, (int?)null, "Fans"), (runOn.Action, runOn.RelayId, Read(runOn.Details, "role")));
        Assert.Equal(T0.AddSeconds(181), _runtime.NextStepAtUtc);

        Assert.True(Plan(State(1, 2), T0.AddSeconds(180)).IsEmpty);
        var end = Plan(State(1, 2), T0.AddSeconds(181));
        Assert.Equal(Mask(1, 2), end.Off);
        Assert.Equal(EventActions.RunOnEnd, Assert.Single(end.Events).Action);

        Plan(State(), T0.AddSeconds(182));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.Null(_runtime.OnSinceUtc);
    }

    [Fact]
    public void Should_TurnOffBothHeaters_AndStartTheFanThatStopped_WhenOneFanStops()
    {
        var trip = Plan(State(1, 3, 4), T0);

        Assert.Equal((Mask(3, 4), Mask(2)), (trip.Off, trip.On));
        var e = Assert.Single(trip.Events);
        Assert.Equal((EventActions.InterlockTrip, (int?)22, EventActors.System), (e.Action, e.RelayId, e.Actor));
        Assert.Equal("""{"missing":"Fan 2","off":["Heater 1","Heater 2"]}""", e.Details);
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);

        // Handled: the heaters' OFF again while the board hasn't reported it, but not logged again.
        var again = Plan(State(1, 3, 4), T0.AddSeconds(1));
        Assert.Equal((Mask(3, 4), ChannelMask.None, 0), (again.Off, again.On, again.Events.Count));

        // The fans are on again and run on, then stop together.
        Assert.Equal(EventActions.RunOnStart, Assert.Single(Plan(State(1, 2), T0.AddSeconds(2)).Events).Action);
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(182)).Off);
    }

    // After a reconnect or a restart, a trip's ON for the fans is waiting with a fresh timeout, and the board shows the
    // heaters on without the fans: they go off at once, not after the timeout.
    [Fact]
    public void Should_TurnOffTheHeatersAtOnce_WhenATripIsWaitingAfterAReconnect()
    {
        (_runtime.Phase, _runtime.TargetLevel, _runtime.PendingStep, _runtime.PendingOn, _runtime.PendingSinceUtc) =
            (GroupPhases.Stopping, 0, 1, true, T0);

        var commands = Plan(State(3, 4), T0.AddSeconds(1));

        Assert.Equal((Mask(3, 4), ChannelMask.None), (commands.Off, commands.On));
        Assert.Empty(commands.Events);
    }

    [Fact]
    public void Should_StartTheWholeStep_WhenAStepAboveIsOff()
    {
        var trip = Plan(State(3), T0);

        Assert.Equal((Mask(3), Mask(1, 2)), (trip.Off, trip.On));
        var e = Assert.Single(trip.Events);
        Assert.Equal((int?)null, e.RelayId);
        Assert.Equal("""{"missing":"Fans","off":["Heater 1"]}""", e.Details);
    }

    [Fact]
    public void Should_LetTheLowestStepOn_BePartlyOn()
    {
        Assert.True(Plan(State(1), T0).IsEmpty);
        Assert.True(Plan(State(1, 2, 3), T0).IsEmpty);
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_RefuseAHeater_UntilBothFansAreOn()
    {
        var refused = new GroupCommands();
        Assert.Equal(new MemberRefusal("Fans"), GroupPlanner.MemberOn(FansHeaters, State(1), _runtime, 3, John, null, T0, refused));
        Assert.True(refused.IsEmpty);

        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 4, John, null, T0, on));
        Assert.Equal(Mask(4), on.On);
        Assert.Equal((EventActions.On, (int?)24), (on.Events[0].Action, on.Events[0].RelayId));
    }

    [Fact]
    public void Should_RunOneFanAlone_AndItsStepMateBesideIt()
    {
        var first = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(), _runtime, 1, John, null, T0, first));
        Assert.Equal(Mask(1), first.On);
        Assert.True(Plan(State(1), T0.AddSeconds(1)).IsEmpty);

        // The relay beside it in its step needs nothing above it.
        var second = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1), _runtime, 2, John, null, T0.AddSeconds(2), second));
        Assert.Equal(Mask(2), second.On);
    }

    [Fact]
    public void Should_TurnOffTheHeatersAtOnce_ThenRunOnOnlyTheFanTurnedOff()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        var off = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0, off);

        Assert.Equal(Mask(3, 4), off.Off);
        Assert.Equal((EventActions.Off, (int?)21), (off.Events[0].Action, off.Events[0].RelayId));

        var runOn = Assert.Single(Plan(State(1, 2), T0.AddSeconds(1)).Events);
        Assert.Equal((EventActions.RunOnStart, (int?)21, "Fan 1"), (runOn.Action, runOn.RelayId, Read(runOn.Details, "role")));

        // Only Fan 1 stops; Fan 2 stays on.
        var end = Plan(State(1, 2), T0.AddSeconds(181));
        Assert.Equal(Mask(1), end.Off);
        Plan(State(2), T0.AddSeconds(182));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.NotNull(_runtime.OnSinceUtc);
        Assert.True(Plan(State(2), T0.AddMinutes(10)).IsEmpty);
    }

    [Fact]
    public void Should_TurnOffOneFanAtOnce_WhenNothingBelowIsOn()
    {
        var off = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0, off);

        Assert.Equal(Mask(2), off.Off);
        Assert.Single(off.Events);
        Plan(State(1), T0.AddSeconds(1));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        Assert.True(Plan(State(1), T0.AddMinutes(10)).IsEmpty);
    }

    [Fact]
    public void Should_StopTheWholeGroup_DuringOneFansRunOn()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        // "Turn off" on the group: both fans go when Fan 1's run-on ends, not only Fan 1, and not sooner.
        var stop = new GroupCommands();
        Assert.True(GroupPlanner.Stop(FansHeaters, State(1, 2), _runtime, John, T0.AddSeconds(2), stop));
        Assert.Equal((ChannelMask.None, EventActions.GroupOff), (stop.Off, Assert.Single(stop.Events).Action));
        Assert.Empty(_runtime.StopRelayIds);
        Assert.True(Plan(State(1, 2), T0.AddSeconds(180)).IsEmpty);
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);
    }

    // The group stops; during the fans' run-on Fan 1 is turned off by hand. The stop goes on: Fan 2 stops too, and
    // nothing is left on without an auto-off.
    [Fact]
    public void Should_KeepTheGroupsStop_WhenOneFanIsTurnedOffDuringItsRunOn()
    {
        _runtime.AutoOffAtUtc = T0.AddHours(2);
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        var off = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0.AddSeconds(30), off);

        Assert.Equal((ChannelMask.None, EventActions.Off), (off.Off, Assert.Single(off.Events).Action));
        Assert.Equal((GroupPhases.Stopping, 0), (_runtime.Phase, _runtime.TargetLevel));
        Assert.Empty(_runtime.StopRelayIds);
        Assert.Equal(T0.AddSeconds(181), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);
        Plan(State(), T0.AddSeconds(182));
        Assert.Equal((GroupPhases.Idle, (DateTime?)null), (_runtime.Phase, _runtime.OnSinceUtc));
    }

    [Fact]
    public void Should_JoinASecondFan_ToTheFirstOnesStop_DuringItsRunOn()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        var off = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(2), off);

        // The fans' step runs on already: Fan 2 goes with Fan 1 when the run-on ends.
        Assert.Equal((ChannelMask.None, EventActions.Off, (int?)22), (off.Off, Assert.Single(off.Events).Action, off.Events[0].RelayId));
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);
        Plan(State(), T0.AddSeconds(182));
        Assert.Equal((GroupPhases.Idle, (DateTime?)null), (_runtime.Phase, _runtime.OnSinceUtc));
    }

    // Fan 2 is turned off while the heaters run: they go off at once. Before the board confirms it, Fan 1 is turned off
    // too: it joins Fan 2's stop, and both run on and go off; nothing is left on.
    [Fact]
    public void Should_JoinASecondFan_ToTheFirstOnesStop_BeforeTheHeatersAreOff()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        _runtime.AutoOffAtUtc = T0.AddHours(2);
        var first = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 2, John, T0, first);
        Assert.Equal(Mask(3, 4), first.Off);

        var second = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0.AddSeconds(0.5), second);
        Assert.Equal((EventActions.Off, (int?)21), (Assert.Single(second.Events).Action, second.Events[0].RelayId));
        Assert.Equal([22, 21], _runtime.StopRelayIds);

        var runOn = Assert.Single(Plan(State(1, 2), T0.AddSeconds(1)).Events);
        Assert.Equal((EventActions.RunOnStart, (int?)null, "Fans"), (runOn.Action, runOn.RelayId, Read(runOn.Details, "role")));
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);

        // One fan confirmed off is not the stop done.
        Assert.True(Plan(State(1), T0.AddSeconds(182)).IsEmpty);
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);
        Plan(State(), T0.AddSeconds(183));
        Assert.Equal((GroupPhases.Idle, (DateTime?)null), (_runtime.Phase, _runtime.OnSinceUtc));
    }

    // Only the fans run. Fan 1 is turned off, and before the board confirms it Fan 2 is too: Fan 2's OFF goes out on the
    // press, not when Fan 1's OFF times out.
    [Fact]
    public void Should_SendASecondFansOff_AtOnce_WhenItsStepsOffIsOnItsWay()
    {
        var first = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, first);
        Assert.Equal(Mask(1), first.Off);

        var second = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), second);

        Assert.Equal(Mask(2), second.Off);
        Assert.True(Plan(State(2), T0.AddSeconds(1)).IsEmpty);
        Plan(State(), T0.AddSeconds(1.5));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_SendASecondFansOff_AtOnce_RightAfterTheRunOn()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        Assert.Equal(Mask(1), Plan(State(1, 2), T0.AddSeconds(181)).Off);

        var second = new GroupCommands();
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(181.5), second);

        Assert.Equal(Mask(2), second.Off);
    }

    // During the fans' run-on Fan 2 is turned off too, then Fan 1 on again: only Fan 1 comes back; Fan 2 still goes
    // off, as asked, and the heaters can't come on while it does.
    [Fact]
    public void Should_BringBackOnlyTheFanTurnedOnAgain()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(2), new GroupCommands());

        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(3), on));
        Assert.True(on.IsEmpty);
        Assert.Equal([22], _runtime.StopRelayIds);
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);

        var heater = new GroupCommands();
        Assert.Equal(new MemberRefusal("Fans"), GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 3, John, 120, T0.AddSeconds(4), heater));
        Assert.True(heater.IsEmpty);

        Assert.Equal(Mask(2), Plan(State(1, 2), T0.AddSeconds(181)).Off);
        Plan(State(1), T0.AddSeconds(182));
        Assert.Equal((GroupPhases.Idle, T0.AddSeconds(3).AddMinutes(120)), (_runtime.Phase, _runtime.AutoOffAtUtc!.Value));
        Assert.True(Plan(State(1), T0.AddMinutes(10)).IsEmpty);
    }

    // Fan 1 has gone off already when it is turned on again; Fan 2 is still on its way off.
    [Fact]
    public void Should_TurnOnAgain_AFanThatIsOffAlready_WhileTheOtherStops()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());

        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(2), _runtime, 1, John, 120, T0.AddSeconds(1), on));

        Assert.Equal((Mask(1), EventActions.On), (on.On, Assert.Single(on.Events).Action));
        Assert.Equal([22], _runtime.StopRelayIds);
    }

    // The group stops; the heaters are off and the fans' OFF is on its way, the board still showing them on. A heater
    // turned on by hand is refused, and the fans' OFF still waits: the heater never runs without its fans.
    [Fact]
    public void Should_RefuseAHeater_WhileTheFansOffIsOnItsWay()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);

        var heater = new GroupCommands();
        Assert.Equal(new MemberRefusal("Fans"), GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 3, John, 120, T0.AddSeconds(181.5), heater));

        Assert.True(heater.IsEmpty);
        Assert.Equal((GroupPhases.Stopping, (int?)1, false), (_runtime.Phase, _runtime.PendingStep, _runtime.PendingOn));
        Plan(State(), T0.AddSeconds(182));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    // The fans' run-on has ended, and their OFF is due on the next pass: a heater is refused all the same.
    [Fact]
    public void Should_RefuseAHeater_WhenTheFansOffIsDue()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        var heater = new GroupCommands();
        Assert.Equal(new MemberRefusal("Fans"), GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 4, John, 120, T0.AddSeconds(181), heater));
        Assert.True(heater.IsEmpty);
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);
    }

    // During the fans' run-on, a heater still calls the stop off, as a fan does: nothing is going off yet.
    [Fact]
    public void Should_CallOffTheStop_WhenAHeaterIsTurnedOnDuringTheFansRunOn()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        var heater = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 3, John, 120, T0.AddSeconds(60), heater));

        Assert.Equal(Mask(3), heater.On);
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    // A stop called off by turning a fan on again: the run gets the group's auto-off back.
    [Fact]
    public void Should_GiveTheAutoOffBack_WhenAStopIsCalledOff()
    {
        _runtime.AutoOffAtUtc = T0.AddHours(2);
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        Assert.Null(_runtime.AutoOffAtUtc);

        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(10), new GroupCommands()));

        Assert.Equal((GroupPhases.Idle, T0.AddSeconds(10).AddMinutes(120), John), (_runtime.Phase, _runtime.AutoOffAtUtc!.Value, _runtime.TurnedOnBy));
    }

    [Fact]
    public void Should_ReadTheEventsOfAStepRight()
    {
        var trip = Plan(State(1, 3, 4), T0).Events[0];
        Assert.Equal("Heater 1 and Heater 2 turned off. Fan 2 must be on first.", Text(trip));

        var stop = new GroupRuntime { GroupId = 1 };
        var commands = new GroupCommands();
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), stop, John, T0, commands);
        GroupPlanner.Plan(FansHeaters, State(1, 2), stop, T0.AddSeconds(1), commands);
        Assert.Equal("for 3 min: Fans", Text(commands.Events.Single(e => e.Action == EventActions.RunOnStart)));
    }

    [Fact]
    public void Should_StopAGroupThatIsOnlyPartlyOn()
    {
        var stop = new GroupCommands();

        Assert.True(GroupPlanner.Stop(FansHeaters, State(1), _runtime, John, T0, stop));

        Assert.Equal(Mask(1), stop.Off);
        Plan(State(), T0.AddSeconds(1));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    [Fact]
    public void Should_StopAStepOnItsWay_WhenTheGroupIsStopped()
    {
        GroupPlanner.Start(FansHeaters, State(), _runtime, John, null, T0, new GroupCommands());

        var stop = new GroupCommands();
        GroupPlanner.Stop(FansHeaters, State(), _runtime, John, T0.AddSeconds(1), stop);

        Assert.Equal(Mask(1, 2), stop.Off);
        Assert.Equal((1, (int?)null, false), (_runtime.PendingStep, _runtime.PendingRelayId, _runtime.PendingOn));
    }

    [Fact]
    public void Should_CarryOnFromTheFans_WhenStartedDuringTheirRunOn()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));

        var start = new GroupCommands();
        GroupPlanner.Start(FansHeaters, State(1, 2), _runtime, John, null, T0.AddSeconds(30), start);

        Assert.Equal((Mask(3, 4), ChannelMask.None), (start.On, start.Off));
    }

    [Fact]
    public void Should_LogTheStepsByName()
    {
        Assert.Equal("""{"steps":["Fans","Heaters"],"delays":[3]}""", GroupPlanner.Chain(FansHeaters));
    }

    [Fact]
    public void Should_NameARelay_ByItsStep_WhenItIsAlone()
    {
        GroupStepInfo fan = new("Fan", 3, 0, [new(11, 1, "Relay 1")]);

        Assert.Equal("Fan", GroupPlanner.NameOf(fan, fan.Relays[0]));
        Assert.Equal("Fan 2", GroupPlanner.NameOf(FansHeaters[0], FansHeaters[0].Relays[1]));
        Assert.Equal(("Heaters", 24), (GroupPlanner.RelayAt(FansHeaters, 4).Step.Name, GroupPlanner.RelayAt(FansHeaters, 4).Relay.RelayId));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupPlanner.RelayAt(FansHeaters, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupPlanner.RelayAt(FansHeaters, 0));
    }

    [Fact]
    public void Should_SayWhichRelaysRun_WhenOnlySomeAre()
    {
        var group = View(new GroupRuntime { GroupId = 1, OnSinceUtc = T0, TurnedOnBy = "john@example.com" });

        Assert.EndsWith("· only Fan 1", Status(group, State(1)), StringComparison.Ordinal);
        Assert.EndsWith("· only Fans, Heater 2", Status(group, State(1, 2, 4)), StringComparison.Ordinal);
        Assert.DoesNotContain("only", Status(group, State(1, 2, 3, 4)), StringComparison.Ordinal);
        Assert.Equal("Out of order · turning off", Status(group, State(1, 3)));
        Assert.Equal("RELAY 1 + 2 → 3 + 4", TestTexts.GroupStatus.Chain(group));
        Assert.Equal(
            [(21, 1, 1, "Fan 1"), (22, 2, 1, "Fan 2"), (23, 3, 2, "Heater 1"), (24, 4, 2, "Heater 2")],
            group.Members.Select(m => (m.RelayId, m.Position, m.Step, m.RoleName)));
    }

    [Fact]
    public void Should_CountDown_OnTheRelaysThatRunOn()
    {
        var stopping = View(new GroupRuntime { GroupId = 1, Phase = GroupPhases.Stopping, NextStepAtUtc = T0.AddSeconds(60) });
        Assert.Equal(["Keeps running for 1 min", "Keeps running for 1 min", "Off", "Off"], Notes(stopping, State(1, 2)));
        Assert.Equal("Stopping · run-on for 1 min: Fans", Status(stopping, State(1, 2)));

        var oneFan = stopping with { Runtime = new GroupRuntime { GroupId = 1, Phase = GroupPhases.Stopping, NextStepAtUtc = T0.AddSeconds(60), StopRelayIds = [21] } };
        Assert.Equal(["Keeps running for 1 min", "On", "Off", "Off"], Notes(oneFan, State(1, 2)));
        Assert.Equal("Stopping · run-on for 1 min: Fan 1", Status(oneFan, State(1, 2)));

        var starting = View(new GroupRuntime { GroupId = 1, Phase = GroupPhases.Starting, NextStepAtUtc = T0.AddSeconds(3) });
        Assert.Equal(["On", "On", "Starts in 3 s", "Starts in 3 s"], Notes(starting, State(1, 2)));
        Assert.Equal("Starting · Heaters in 3 s", Status(starting, State(1, 2)));
    }

    [Fact]
    public void Should_NeedTheWholeStepAbove()
    {
        Assert.Equal("Fans", GroupPlanner.NeedsOn(FansHeaters, State(2), 3));
        Assert.Null(GroupPlanner.NeedsOn(FansHeaters, State(1, 2), 3));
        Assert.Null(GroupPlanner.NeedsOn(FansHeaters, State(), 2));
    }

    private static string Text(GroupEvent e) =>
        TestTexts.Events.For(new EventLogEntry { GroupId = 1, RelayId = e.RelayId, Actor = e.Actor, Action = e.Action, Details = e.Details }, null, null, "Workshop heat").What;

    private GroupCommands Plan(BoardState state, DateTime now)
    {
        var commands = new GroupCommands();
        GroupPlanner.Plan(FansHeaters, state, _runtime, now, commands);
        return commands;
    }

    private static GroupView View(GroupRuntime runtime) => new(1, "Workshop heat", GroupIcons.Fan, 120, FansHeaters, runtime);

    private static string Status(GroupView group, BoardState state) =>
        TestTexts.GroupStatus.For(group, state, T0, t => t, who => who == "john@example.com" ? "John" : who);

    private static List<string> Notes(GroupView group, BoardState state) =>
        [.. Enumerable.Range(0, group.Members.Count).Select(i => TestTexts.GroupStatus.MemberNote(group, state, i, state.IsOn(group.Members[i].Channel), null, T0))];

    private static string? Read(string? details, string name) =>
        details is null ? null : JsonDocument.Parse(details).RootElement.GetProperty(name).GetString();

    private static ChannelMask Mask(params int[] channels) => ChannelMask.Of(channels);

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, T0);
}
