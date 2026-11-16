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

    // Issue #353, the first race. Fan 1 is off already when it is turned on again while Fan 2 stops, and the board shows
    // Fan 2's OFF before Fan 1's ON: the stop waits for that ON, so the run stays John's and isn't taken for one turned
    // on outside the app.
    [Fact]
    public void Should_FinishTheStop_OnlyOnceAFanTurnedOnAgainShowsOn()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        _runtime.TurnedOnBy = John;
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(2), T0.AddSeconds(0.2));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
        GroupPlanner.MemberOff(FansHeaters, State(2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());

        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(2), _runtime, 1, John, 120, T0.AddSeconds(1), on));
        Assert.Equal((Mask(1), EventActions.On, (int?)21), (on.On, Assert.Single(on.Events).Action, on.Events[0].RelayId));

        // Nothing on: Fan 2's OFF has landed, Fan 1's ON is still on its way.
        Assert.True(Plan(State(), T0.AddSeconds(1.2), onOnItsWay: Mask(1)).IsEmpty);
        Assert.Equal((GroupPhases.Stopping, John, T0.AddMinutes(-5)), (_runtime.Phase, _runtime.TurnedOnBy, _runtime.OnSinceUtc!.Value));

        // Fan 1's ON lands: the stop is done, and Fan 1 runs on John's run.
        Plan(State(1), T0.AddSeconds(1.4), onOnItsWay: Mask(1));
        GroupPlanner.GiveOwnerlessAutoOff(FansHeaters, State(1), _runtime, 120, T0.AddSeconds(1.4));
        Assert.Equal((GroupPhases.Idle, John, T0.AddMinutes(-5)), (_runtime.Phase, _runtime.TurnedOnBy, _runtime.OnSinceUtc!.Value));
        Assert.Empty(_runtime.StopRelayIds);
    }

    // Issue #353, the second race. Fan 1's own OFF went out and the board still shows it on when it is turned on again:
    // its ON goes out and is logged, takes that OFF over, and the stop of Fan 2 waits for it.
    [Fact]
    public void Should_SendLogAndWaitForAFansOn_WhenItsOwnOffIsOnItsWay()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        _runtime.TurnedOnBy = John;
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());

        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(0.7), on, Mask(1, 2)));
        Assert.Equal((Mask(1), ChannelMask.None), (on.On, on.Off));
        var pressed = Assert.Single(on.Events);
        Assert.Equal((EventActions.On, (int?)21, John), (pressed.Action, pressed.RelayId, pressed.Actor));
        Assert.Equal([22], _runtime.StopRelayIds);

        // Both OFFs land before the ON.
        Assert.True(Plan(State(), T0.AddSeconds(0.9), onOnItsWay: Mask(1)).IsEmpty);
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);

        Plan(State(1), T0.AddSeconds(1.1), onOnItsWay: Mask(1));
        Assert.Equal((GroupPhases.Idle, John, T0.AddMinutes(-5)), (_runtime.Phase, _runtime.TurnedOnBy, _runtime.OnSinceUtc!.Value));
    }

    // The ON never shows (the board didn't carry it out): once it is no longer on its way, the stop is done and the
    // run ends, as for any group that is off.
    [Fact]
    public void Should_FinishTheStop_WhenAFansOnNeverShows()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        _runtime.TurnedOnBy = John;
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());
        GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(0.7), new GroupCommands(), Mask(1, 2));
        Plan(State(), T0.AddSeconds(0.9), onOnItsWay: Mask(1));

        Plan(State(), T0.AddSeconds(6));

        Assert.Equal((GroupPhases.Idle, (DateTime?)null, (string?)null), (_runtime.Phase, _runtime.OnSinceUtc, _runtime.TurnedOnBy));
    }

    // An ON on its way for a relay the stop turns off doesn't hold the stop up: only one turned on again does.
    [Fact]
    public void Should_NotWaitForAnOn_OfARelayTheStopTurnsOff()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0, new GroupCommands());

        Plan(State(1), T0.AddSeconds(0.5), onOnItsWay: Mask(2));

        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    // Issue #398. Fan 1 is turned on again while Fan 2 stops, and the board shows Fan 2 off before Fan 1's ON. "Turn off"
    // then turns Fan 1 off as well: its OFF goes at once, after its ON, and the stop is done only once that ON is no
    // longer on its way and the board shows Fan 1 off, so the run isn't taken for one outside the app in between.
    [Fact]
    public void Should_TurnOffAFanTurnedOnAgain_WhenTheGroupIsTurnedOffBeforeItsOnShows()
    {
        _runtime.OnSinceUtc = T0.AddMinutes(-5);
        _runtime.TurnedOnBy = John;
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(2), T0.AddSeconds(0.2));
        GroupPlanner.MemberOff(FansHeaters, State(2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());
        GroupPlanner.MemberOn(FansHeaters, State(2), _runtime, 1, John, 120, T0.AddSeconds(1), new GroupCommands());
        Plan(State(), T0.AddSeconds(1.2), onOnItsWay: Mask(1));
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);

        var stop = new GroupCommands();
        Assert.True(GroupPlanner.Stop(FansHeaters, State(), _runtime, John, T0.AddSeconds(1.3), stop, Mask(1)));

        Assert.Equal((ChannelMask.None, Mask(1)), (stop.On, stop.Off));
        Assert.Contains(stop.Events, e => e.Action == EventActions.GroupOff);
        Assert.Equal((1, false), (_runtime.PendingStep, _runtime.PendingOn));

        // The board shows nothing on while the ON is on its way, then Fan 1 on: neither is that OFF done.
        Assert.True(Plan(State(), T0.AddSeconds(1.4), onOnItsWay: Mask(1)).IsEmpty);
        Assert.True(Plan(State(1), T0.AddSeconds(1.5)).IsEmpty);
        GroupPlanner.GiveOwnerlessAutoOff(FansHeaters, State(1), _runtime, 120, T0.AddSeconds(1.5));
        Assert.Equal((GroupPhases.Stopping, John), (_runtime.Phase, _runtime.TurnedOnBy));

        // Fan 1's OFF lands: the group is off, and its run ends.
        Plan(State(), T0.AddSeconds(1.6));
        Plan(State(), T0.AddSeconds(1.7));
        Assert.Equal((GroupPhases.Idle, (DateTime?)null, (string?)null), (_runtime.Phase, _runtime.OnSinceUtc, _runtime.TurnedOnBy));
    }

    // Issue #398, with Fan 2's OFF still on its way when "Turn off" comes: Fan 1, which the board still shows on from
    // before its own OFF, goes with it at once, and the stop waits for both.
    [Fact]
    public void Should_SendAFansOff_WithItsStepsOff_WhenTheGroupIsTurnedOffWhileItsOnIsOnItsWay()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());
        GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(0.7), new GroupCommands(), Mask(1, 2));

        var stop = new GroupCommands();
        GroupPlanner.Stop(FansHeaters, State(1, 2), _runtime, John, T0.AddSeconds(0.8), stop);

        Assert.Equal((ChannelMask.None, Mask(1)), (stop.On, stop.Off));
        Assert.Equal((1, false, T0.AddSeconds(0.8)), (_runtime.PendingStep, _runtime.PendingOn, _runtime.PendingSinceUtc!.Value));
        Assert.True(Plan(State(), T0.AddSeconds(0.9), onOnItsWay: Mask(1)).IsEmpty);
        Assert.True(Plan(State(1), T0.AddSeconds(1)).IsEmpty);
        Plan(State(), T0.AddSeconds(1.1));
        Plan(State(), T0.AddSeconds(1.2));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    // Issue #398, with Fan 2's OFF still on its way and Fan 1's own OFF already shown: the board shows only Fan 2 on,
    // and Fan 1's ON is on its way. "Turn off" sends Fan 1's OFF at once with its step's, not 5 s later when that OFF
    // times out.
    [Fact]
    public void Should_SendAFansOff_WithItsStepsOff_WhenTheBoardDoesntShowItsOnYet()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());
        GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(0.7), new GroupCommands(), Mask(1, 2));

        var stop = new GroupCommands();
        GroupPlanner.Stop(FansHeaters, State(2), _runtime, John, T0.AddSeconds(0.8), stop, Mask(1));

        Assert.Equal((ChannelMask.None, Mask(1)), (stop.On, stop.Off));
        Assert.Equal((1, false, T0.AddSeconds(0.8)), (_runtime.PendingStep, _runtime.PendingOn, _runtime.PendingSinceUtc!.Value));
        Assert.True(Plan(State(), T0.AddSeconds(0.9), onOnItsWay: Mask(1)).IsEmpty);
        Assert.True(Plan(State(1), T0.AddSeconds(1)).IsEmpty);
        Plan(State(), T0.AddSeconds(1.1));
        Plan(State(), T0.AddSeconds(1.2));
        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
    }

    // Issue #398, one step lower. Heater 1 is turned on again while Heater 2 stops, and "Turn off" comes before its ON
    // shows. The heater goes off first, after its ON; the fans stay on and then run on. Turning the fans off at once
    // would leave the heater on below them once its ON lands.
    [Fact]
    public void Should_TurnOffAHeaterTurnedOnAgain_BeforeTheFans()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 3, 4), _runtime, 3, John, T0, new GroupCommands());
        Plan(State(1, 2, 4), T0.AddSeconds(0.2));
        GroupPlanner.MemberOff(FansHeaters, State(1, 2, 4), _runtime, 4, John, T0.AddSeconds(0.5), new GroupCommands());
        var on = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2, 4), _runtime, 3, John, 120, T0.AddSeconds(1), on));
        Assert.Equal(Mask(3), on.On);
        Plan(State(1, 2), T0.AddSeconds(1.2), onOnItsWay: Mask(3));
        Assert.Equal(GroupPhases.Stopping, _runtime.Phase);

        var stop = new GroupCommands();
        GroupPlanner.Stop(FansHeaters, State(1, 2), _runtime, John, T0.AddSeconds(1.3), stop, Mask(3));

        Assert.Equal((ChannelMask.None, Mask(3)), (stop.On, stop.Off));
        Assert.True(Plan(State(1, 2, 3), T0.AddSeconds(1.4)).IsEmpty);

        // Once the heater is off, the fans run on, then stop.
        var runOn = Plan(State(1, 2), T0.AddSeconds(1.5));
        Assert.Equal(ChannelMask.None, runOn.Off);
        Assert.Contains(runOn.Events, e => e.Action == EventActions.RunOnStart);
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181.5)).Off);
    }

    // Fan 1's ON never shows (the board didn't carry it out): the stop waits no longer than that ON counts as on
    // its way, and its OFF is done then.
    [Fact]
    public void Should_GoOnWithTheStop_WhenATurnedOnAgainRelaysOnNeverShows()
    {
        GroupPlanner.MemberOff(FansHeaters, State(1, 2), _runtime, 1, John, T0, new GroupCommands());
        Plan(State(2), T0.AddSeconds(0.2));
        GroupPlanner.MemberOff(FansHeaters, State(2), _runtime, 2, John, T0.AddSeconds(0.5), new GroupCommands());
        GroupPlanner.MemberOn(FansHeaters, State(2), _runtime, 1, John, 120, T0.AddSeconds(1), new GroupCommands());
        Plan(State(), T0.AddSeconds(1.2), onOnItsWay: Mask(1));
        GroupPlanner.Stop(FansHeaters, State(), _runtime, John, T0.AddSeconds(1.3), new GroupCommands(), Mask(1));

        Assert.True(Plan(State(), T0.AddSeconds(5), onOnItsWay: Mask(1)).IsEmpty);
        Plan(State(), T0.AddSeconds(6));
        Plan(State(), T0.AddSeconds(6));

        Assert.Equal(GroupPhases.Idle, _runtime.Phase);
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

    // The group stops; the fans' OFF is on its way, the board still showing them on. Fan 1 is turned on again: its ON
    // goes out and takes its OFF over, Fan 2 still goes off. A heater right after is refused: no heater ON while the
    // fans' OFF is in flight.
    [Fact]
    public void Should_SendAFansOn_AndRefuseAHeater_WhileTheFansOffIsInFlight()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        Assert.Equal(Mask(1, 2), Plan(State(1, 2), T0.AddSeconds(181)).Off);

        var fan = new GroupCommands();
        Assert.Null(GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 1, John, 120, T0.AddSeconds(181.2), fan));
        Assert.Equal((Mask(1), EventActions.On), (fan.On, Assert.Single(fan.Events).Action));
        Assert.Equal([22], _runtime.StopRelayIds);
        Assert.Equal((GroupPhases.Stopping, (int?)1, false), (_runtime.Phase, _runtime.PendingStep, _runtime.PendingOn));

        var heater = new GroupCommands();
        Assert.Equal(new MemberRefusal("Fans"), GroupPlanner.MemberOn(FansHeaters, State(1, 2), _runtime, 3, John, 120, T0.AddSeconds(181.4), heater));
        Assert.True(heater.IsEmpty);

        // Fan 2 off: the stop is done, Fan 1 runs with the group's auto-off.
        Plan(State(1), T0.AddSeconds(182));
        Assert.Equal((GroupPhases.Idle, T0.AddSeconds(181.2).AddMinutes(120)), (_runtime.Phase, _runtime.AutoOffAtUtc!.Value));
    }

    // The fans' OFF is on its way when the group is started again: the status from before that OFF, still showing the
    // fans on, doesn't confirm them; the heaters come only after the board shows the fans on again, and their wait.
    [Fact]
    public void Should_StartTheHeaters_OnlyAfterTheBoardShowsTheFansOnAgain()
    {
        GroupPlanner.Stop(FansHeaters, State(1, 2, 3, 4), _runtime, John, T0, new GroupCommands());
        Plan(State(1, 2), T0.AddSeconds(1));
        Plan(State(1, 2), T0.AddSeconds(181));

        var start = new GroupCommands();
        GroupPlanner.Start(FansHeaters, State(1, 2), _runtime, John, null, T0.AddSeconds(181.2), start);
        Assert.Equal(Mask(1, 2), start.On);
        Assert.Equal((GroupPhases.Starting, (int?)1, true), (_runtime.Phase, _runtime.PendingStep, _runtime.PendingOn));

        // As the sequencer passes it: the fans' OFF not yet shown.
        var stale = new GroupCommands();
        GroupPlanner.Plan(FansHeaters, State(1, 2), _runtime, T0.AddSeconds(182), stale, Mask(1, 2));
        Assert.True(stale.IsEmpty);
        Assert.Null(_runtime.NextStepAtUtc);

        Assert.True(Plan(State(), T0.AddSeconds(182.5)).IsEmpty);
        Assert.True(Plan(State(1, 2), T0.AddSeconds(183)).IsEmpty);
        Assert.Equal(T0.AddSeconds(186), _runtime.NextStepAtUtc);
        Assert.Equal(Mask(3, 4), Plan(State(1, 2), T0.AddSeconds(186)).On);
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
        Assert.Equal(["Keeps running for 1 min", "Off"], Notes(stopping, State(1, 2)));
        Assert.Equal("Stopping · run-on for 1 min: Fans", Status(stopping, State(1, 2)));

        var oneFan = stopping with { Runtime = new GroupRuntime { GroupId = 1, Phase = GroupPhases.Stopping, NextStepAtUtc = T0.AddSeconds(60), StopRelayIds = [21] } };
        Assert.Equal(["Keeps running for 1 min", "Off"], Notes(oneFan, State(1, 2)));
        Assert.Equal("Stopping · run-on for 1 min: Fan 1", Status(oneFan, State(1, 2)));

        var starting = View(new GroupRuntime { GroupId = 1, Phase = GroupPhases.Starting, NextStepAtUtc = T0.AddSeconds(3) });
        Assert.Equal(["On", "Starts in 3 s"], Notes(starting, State(1, 2)));
        Assert.Equal("Starting · Heaters in 3 s", Status(starting, State(1, 2)));
    }

    [Fact]
    public void Should_SayWhichRelaysOfAStepAreOn_OnTheGroupScreen()
    {
        var group = View(new GroupRuntime { GroupId = 1 });

        Assert.Equal(["Only Fan 1 on", "Needs Fans to be on"], Notes(group, State(1)));
        Assert.Equal(["On", "Only Heater 2 on"], Notes(group, State(1, 2, 4)));
        Assert.Equal(["Off · starts first", "Needs Fans to be on"], Notes(group, State()));

        // Two of three relays of a step on.
        GroupStepInfo threeFans = new("Fans", 3, 0, [new(21, 1, "Fan 1"), new(22, 2, "Fan 2"), new(25, 5, "Fan 3")]);
        var three = group with { Steps = [threeFans, FansHeaters[1]] };
        Assert.Equal(["Only Fan 1, Fan 2 on", "Needs Fans to be on"], Notes(three, State(1, 2)));
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

    private GroupCommands Plan(BoardState state, DateTime now, ChannelMask onOnItsWay = default)
    {
        var commands = new GroupCommands();
        GroupPlanner.Plan(FansHeaters, state, _runtime, now, commands, onOnItsWay: onOnItsWay);
        return commands;
    }

    private static GroupView View(GroupRuntime runtime) => new(1, "Workshop heat", GroupIcons.Fan, 120, FansHeaters, runtime);

    private static string Status(GroupView group, BoardState state) =>
        TestTexts.GroupStatus.For(group, state, T0, t => t, who => who == "john@example.com" ? "John" : who);

    private static List<string> Notes(GroupView group, BoardState state) =>
        [.. Enumerable.Range(0, group.Steps.Count).Select(i => TestTexts.GroupStatus.StepNote(group, state, i, T0))];

    private static string? Read(string? details, string name) =>
        details is null ? null : JsonDocument.Parse(details).RootElement.GetProperty(name).GetString();

    private static ChannelMask Mask(params int[] channels) => ChannelMask.Of(channels);

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, T0);
}
