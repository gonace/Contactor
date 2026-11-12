using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>A relay group's events in Events, and the status line under a group's name.</summary>
public sealed class GroupTextTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 14, 2, 0, DateTimeKind.Utc);

    private static readonly GroupView FanHeater = new(
        1,
        "Fan heater",
        GroupIcons.Fan,
        120,
        [new GroupMemberView(1, 1, "Fan", 3, 300), new GroupMemberView(2, 2, "Heat", 3, 0)],
        new GroupRuntime { GroupId = 1 });

    [Fact]
    public void Should_TellWhoStartedTheGroup_AndInWhatOrder()
    {
        var view = EventText.For(
            Entry("john@example.com", EventActions.GroupOn, """{"steps":["Fan","Heat"],"delays":[3]}"""),
            relayName: null,
            personName: "John",
            groupName: "Fan heater");

        Assert.Equal(("John", "turned on the group", "Fan heater · Fan → 3 s → Heat", false), (view.Who, view.What, view.Target, view.IsAlert));
    }

    [Theory]
    [InlineData(EventActions.RunOnStart, """{"seconds":300,"role":"Fan"}""", "Run-on:", "Fan keeps running for 5 min")]
    [InlineData(EventActions.RunOnEnd, """{"role":"Fan"}""", "Run-on done:", "Fan off")]
    [InlineData(EventActions.GroupOff, """{"afterMinutes":120}""", "Auto-off", "turned off the group after 2 h")]
    public void Should_DescribeWhatTheGroupDidByItself(string action, string details, string who, string what)
    {
        var actor = action == EventActions.GroupOff ? EventActors.AutoOff : EventActors.System;

        var view = EventText.For(Entry(actor, action, details), "Relay 1", null, "Fan heater");

        Assert.Equal((who, what, "Fan heater", "A"), (view.Who, view.What, view.Target, view.Badge));
        Assert.False(view.IsAlert);
    }

    [Fact]
    public void Should_ShowASafetyShutOffInRed()
    {
        var view = EventText.For(
            Entry(EventActors.System, EventActions.InterlockTrip, """{"missing":"Fan","off":["Heat"]}"""),
            "Relay 1",
            null,
            "Fan heater");

        Assert.Equal(("Safety shut-off:", "Heat turned off because Fan was off", true), (view.Who, view.What, view.IsAlert));
    }

    [Fact]
    public void Should_ShowAStartThatFailedInRed()
    {
        var view = EventText.For(Entry(EventActors.System, EventActions.GroupStartFailed, """{"step":2,"role":"Heat"}"""), "Relay 2", null, "Fan heater");

        Assert.Equal(("The group", "couldn't start Heat", true), (view.Who, view.What, view.IsAlert));
    }

    [Fact]
    public void Should_NameADeletedGroup_FromItsEvent()
    {
        var view = EventText.For(Entry("john@example.com", EventActions.GroupDelete, """{"name":"Painting"}"""), null, "John", groupName: null);

        Assert.Equal(("deleted the group", "Painting"), (view.What, view.Target));
    }

    [Fact]
    public void Should_ReadMalformedDetailsWithoutFailing()
    {
        var view = EventText.For(Entry(EventActors.System, EventActions.InterlockTrip, "{not json"), null, null, "Fan heater");

        Assert.Equal("the relay above was started", view.What);
    }

    [Fact]
    public void Should_ShowAPlainOnForARelayOfAGroup()
    {
        var view = EventText.For(Entry("john@example.com", EventActions.On, null), "Relay 1", "John", "Fan heater");

        Assert.Equal(("turned on", "Relay 1"), (view.What, view.Target));
    }

    [Fact]
    public void Should_SayStartingWithTheStep()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting } };

        Assert.Equal("Starting · step 2 of 2", Status(group, State(1)));
    }

    [Fact]
    public void Should_CountDownTheStartDelay()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting, NextStepAtUtc = Now.AddSeconds(2.4) } };

        Assert.Equal("Starting · Heat in 3 s", Status(group, State(1)));
        Assert.Equal("Starting · step 2 of 2", Status(group with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting, NextStepAtUtc = Now } }, State(1)));
    }

    [Fact]
    public void Should_CountDownOnTheRelayThatIsNext_OnTheGroupScreen()
    {
        var starting = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting, NextStepAtUtc = Now.AddSeconds(3) } };
        var stopping = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Stopping, NextStepAtUtc = Now.AddSeconds(299) } };

        Assert.Equal("On", GroupStatusText.MemberNote(starting, State(1), 0, on: true, null, Now));
        Assert.Equal("Starts in 3 s", GroupStatusText.MemberNote(starting, State(1), 1, on: false, null, Now));
        Assert.Equal("Keeps running for 4 min 59 s", GroupStatusText.MemberNote(stopping, State(1), 0, on: true, null, Now));
        Assert.Equal("Off", GroupStatusText.MemberNote(stopping, State(1), 1, on: false, null, Now));
        Assert.Equal("Needs Fan to be on", GroupStatusText.MemberNote(FanHeater, State(), 1, on: false, "Fan", Now));
    }

    [Fact]
    public void Should_SayHowLongTheRunOnLasts()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Stopping, NextStepAtUtc = Now.AddSeconds(241) } };

        Assert.Equal("Stopping · Fan keeps running for 4 min 1 s", Status(group, State(1)));
        Assert.Equal("Stopping", Status(group, State()));
    }

    [Fact]
    public void Should_SayWhoAndUntilWhen_WhenOn()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { OnSinceUtc = Now, TurnedOnBy = "john@example.com", AutoOffAtUtc = Now.AddHours(2) } };

        Assert.Equal("On since 14:02 · John · off 16:02", Status(group, State(1, 2)));
        Assert.Equal("On since 14:02 · John · off 16:02 · only Fan", Status(group, State(1)));
        Assert.Equal("Off", Status(group, State()));
    }

    [Fact]
    public void Should_SayARelayIsOutOfOrder_WhenTheOneAboveIsOff()
    {
        Assert.Equal("Out of order · turning off", Status(FanHeater, State(2)));
    }

    [Fact]
    public void Should_ShowAGroupWithoutRelaysAsOff()
    {
        var empty = FanHeater with { Members = [], Runtime = new GroupRuntime { Phase = GroupPhases.Starting } };

        Assert.Equal("Off", Status(empty, State()));
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(300, "5 min")]
    [InlineData(5400, "90 min")]
    [InlineData(7200, "2 h")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(7201, "120 min 1 s")]
    [InlineData(int.MaxValue, "35791394 min 7 s")]
    [InlineData(-1, "a while")]
    public void Should_WriteSecondsForPeople(int seconds, string expected)
    {
        Assert.Equal(expected, EventText.Seconds(seconds));
    }

    [Fact]
    public void Should_SayWhenTheScheduleStartsTheGroup_WhenOff()
    {
        var outlook = new RelayOutlook(null, new DateTime(2026, 11, 9, 6, 30, 0, DateTimeKind.Utc));

        Assert.Equal("Schedule: on tomorrow 06:30", Status(FanHeater, State(), outlook));
    }

    [Fact]
    public void Should_SayWhenTheRunEnds_ForAGroupTheScheduleStarted()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { OnSinceUtc = Now, TurnedOnBy = EventActors.Schedule, AutoOffAtUtc = Now.AddHours(2) } };
        var outlook = new RelayOutlook(Now.AddMinutes(58), null);

        Assert.Equal("On since 14:02 · schedule · off 15:00", Status(group, State(1, 2), outlook));
    }

    [Theory]
    [InlineData(0, "1 s")]
    [InlineData(1, "1 s")]
    [InlineData(40.2, "41 s")]
    [InlineData(59.5, "1 min")]
    [InlineData(60, "1 min")]
    [InlineData(61, "1 min 1 s")]
    [InlineData(299, "4 min 59 s")]
    [InlineData(299.1, "5 min")]
    [InlineData(1800, "30 min")]
    public void Should_CountDownTheTimeLeft(double seconds, string expected)
    {
        Assert.Equal(expected, GroupStatusText.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Should_CountTheGroupAsOn_UnlessItIsStopping()
    {
        Assert.True(FanHeater.IsOn(State(1)));
        Assert.False((FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Stopping } }).IsOn(State(1)));
        Assert.True((FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting } }).IsOn(State()));
        Assert.False(FanHeater.IsOn(State()));
    }

    private static string Status(GroupView group, BoardState state, RelayOutlook? outlook = null) =>
        GroupStatusText.For(group, state, Now, utc => utc, email => email switch
        {
            "john@example.com" => "John",
            EventActors.Schedule => "schedule",
            _ => email,
        }, outlook);

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, Now);

    private static EventLogEntry Entry(string actor, string action, string? details) =>
        new() { TimestampUtc = Now, GroupId = 1, RelayId = 1, Actor = actor, Action = action, Details = details };
}
