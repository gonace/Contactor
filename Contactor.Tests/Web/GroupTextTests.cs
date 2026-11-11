using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>A relay group's events in Händelser, and the status line under a group's name.</summary>
public sealed class GroupTextTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 14, 2, 0, DateTimeKind.Utc);

    private static readonly GroupView FanHeater = new(
        1,
        "Värmefläkt",
        GroupIcons.Fan,
        120,
        [new GroupMemberView(1, 1, "Fläkt", 3, 300), new GroupMemberView(2, 2, "Värme", 3, 0)],
        new GroupRuntime { GroupId = 1 });

    [Fact]
    public void Should_TellWhoStartedTheGroup_AndInWhatOrder()
    {
        var view = EventText.For(
            Entry("john@example.com", EventActions.GroupOn, """{"steps":["Fläkt","Värme"],"delays":[3]}"""),
            relayName: null,
            personName: "John",
            groupName: "Värmefläkt");

        Assert.Equal(("John", "slog på gruppen", "Värmefläkt · Fläkt → 3 s → Värme", false), (view.Who, view.What, view.Target, view.IsAlert));
    }

    [Theory]
    [InlineData(EventActions.RunOnStart, """{"seconds":300,"role":"Fläkt"}""", "Eftergång:", "Fläkt går 5 min till")]
    [InlineData(EventActions.RunOnEnd, """{"role":"Fläkt"}""", "Eftergång klar:", "Fläkt avstängd")]
    [InlineData(EventActions.GroupOff, """{"afterMinutes":120}""", "Auto-av", "stängde av gruppen efter 2 h")]
    public void Should_DescribeWhatTheGroupDidByItself(string action, string details, string who, string what)
    {
        var actor = action == EventActions.GroupOff ? EventActors.AutoOff : EventActors.System;

        var view = EventText.For(Entry(actor, action, details), "Relä 1", null, "Värmefläkt");

        Assert.Equal((who, what, "Värmefläkt", "A"), (view.Who, view.What, view.Target, view.Badge));
        Assert.False(view.IsAlert);
    }

    [Fact]
    public void Should_ShowASafetyShutOffInRed()
    {
        var view = EventText.For(
            Entry(EventActors.System, EventActions.InterlockTrip, """{"missing":"Fläkt","off":["Värme"]}"""),
            "Relä 1",
            null,
            "Värmefläkt");

        Assert.Equal(("Säkerhetsavstängning:", "Värme stängdes av eftersom Fläkt var av", true), (view.Who, view.What, view.IsAlert));
    }

    [Fact]
    public void Should_ShowAStartThatFailedInRed()
    {
        var view = EventText.For(Entry(EventActors.System, EventActions.GroupStartFailed, """{"step":2,"role":"Värme"}"""), "Relä 2", null, "Värmefläkt");

        Assert.Equal(("Gruppen", "kunde inte starta Värme", true), (view.Who, view.What, view.IsAlert));
    }

    [Fact]
    public void Should_NameADeletedGroup_FromItsEvent()
    {
        var view = EventText.For(Entry("john@example.com", EventActions.GroupDelete, """{"name":"Lackering"}"""), null, "John", groupName: null);

        Assert.Equal(("tog bort gruppen", "Lackering"), (view.What, view.Target));
    }

    [Fact]
    public void Should_ReadMalformedDetailsWithoutFailing()
    {
        var view = EventText.For(Entry(EventActors.System, EventActions.InterlockTrip, "{not json"), null, null, "Värmefläkt");

        Assert.Equal("reläet ovanför startades", view.What);
    }

    [Fact]
    public void Should_ShowAPlainOnForARelayOfAGroup()
    {
        var view = EventText.For(Entry("john@example.com", EventActions.On, null), "Relä 1", "John", "Värmefläkt");

        Assert.Equal(("slog på", "Relä 1"), (view.What, view.Target));
    }

    [Fact]
    public void Should_SayStartingWithTheStep()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting } };

        Assert.Equal("Startar · steg 2 av 2", Status(group, State(1)));
    }

    [Fact]
    public void Should_SayHowLongTheRunOnLasts()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Stopping, NextStepAtUtc = Now.AddSeconds(241) } };

        Assert.Equal("Stänger av · Fläkt går 5 min till", Status(group, State(1)));
        Assert.Equal("Stänger av", Status(group, State()));
    }

    [Fact]
    public void Should_SayWhoAndUntilWhen_WhenOn()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { OnSinceUtc = Now, TurnedOnBy = "john@example.com", AutoOffAtUtc = Now.AddHours(2) } };

        Assert.Equal("På sedan 14:02 · John · av 16:02", Status(group, State(1, 2)));
        Assert.Equal("På sedan 14:02 · John · av 16:02 · bara Fläkt", Status(group, State(1)));
        Assert.Equal("Av", Status(group, State()));
    }

    [Fact]
    public void Should_SayARelayIsOutOfOrder_WhenTheOneAboveIsOff()
    {
        Assert.Equal("I fel ordning · stängs av", Status(FanHeater, State(2)));
    }

    [Fact]
    public void Should_ShowAGroupWithoutRelaysAsOff()
    {
        var empty = FanHeater with { Members = [], Runtime = new GroupRuntime { Phase = GroupPhases.Starting } };

        Assert.Equal("Av", Status(empty, State()));
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(300, "5 min")]
    [InlineData(5400, "90 min")]
    [InlineData(7200, "2 h")]
    [InlineData(7201, "121 min")]
    [InlineData(int.MaxValue, "35791395 min")]
    [InlineData(-1, "en stund")]
    public void Should_WriteSecondsForPeople(int seconds, string expected)
    {
        Assert.Equal(expected, EventText.Seconds(seconds));
    }

    [Fact]
    public void Should_SayWhenTheScheduleStartsTheGroup_WhenOff()
    {
        var outlook = new RelayOutlook(null, new DateTime(2026, 11, 9, 6, 30, 0, DateTimeKind.Utc));

        Assert.Equal("Schema: på i morgon 06:30", Status(FanHeater, State(), outlook));
    }

    [Fact]
    public void Should_SayWhenTheRunEnds_ForAGroupTheScheduleStarted()
    {
        var group = FanHeater with { Runtime = new GroupRuntime { OnSinceUtc = Now, TurnedOnBy = EventActors.Schedule, AutoOffAtUtc = Now.AddHours(2) } };
        var outlook = new RelayOutlook(Now.AddMinutes(58), null);

        Assert.Equal("På sedan 14:02 · schema · av 15:00", Status(group, State(1, 2), outlook));
    }

    [Theory]
    [InlineData(1, "1 s")]
    [InlineData(40.2, "41 s")]
    [InlineData(60, "1 min")]
    [InlineData(61, "2 min")]
    public void Should_RoundTheTimeLeftUp(double seconds, string expected)
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
            EventActors.Schedule => "schema",
            _ => email,
        }, outlook);

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, Now);

    private static EventLogEntry Entry(string actor, string action, string? details) =>
        new() { TimestampUtc = Now, GroupId = 1, RelayId = 1, Actor = actor, Action = action, Details = details };
}
