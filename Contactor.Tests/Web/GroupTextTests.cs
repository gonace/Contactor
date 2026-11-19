using Contactor.Boards;
using Contactor.Web.Features.Groups;
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
        [new GroupStepInfo("Fan", 3, 300, [new(11, 1, "Fan")]), new GroupStepInfo("Heat", 3, 0, [new(12, 2, "Heat")])],
        new GroupRuntime { GroupId = 1 });

    [Theory]
    [InlineData(EventActions.RunOnStart, """{"seconds":300,"role":"Fan"}""", "Run-on", "for 5 min: Fan")]
    [InlineData(EventActions.RunOnEnd, """{"role":"Fan"}""", "Run-on done:", "Fan off")]
    [InlineData(EventActions.GroupOff, """{"afterMinutes":120}""", "Auto-off", "turned off the group after 2 h")]
    public void Should_DescribeWhatTheGroupDidByItself(string action, string details, string who, string what)
    {
        var actor = action == EventActions.GroupOff ? EventActors.AutoOff : EventActors.System;

        var view = TestTexts.Events.For(Entry(actor, action, details), "Relay 1", null, "Fan heater");

        Assert.Equal((who, what, "Fan heater", "A"), (view.Who, view.What, view.Target, view.Badge));
        Assert.False(view.IsAlert);
    }

    // A step's name may be plural ("Fans", Issue #326): no verb in the line agrees with it.
    [Theory]
    [InlineData(EventActions.RunOnStart, """{"seconds":300,"role":"Fan"}""", "Run-on for 5 min: Fan")]
    [InlineData(EventActions.RunOnStart, """{"seconds":180,"role":"Fans"}""", "Run-on for 3 min: Fans")]
    [InlineData(EventActions.RunOnEnd, """{"role":"Fan"}""", "Run-on done: Fan off")]
    [InlineData(EventActions.RunOnEnd, """{"role":"Fans"}""", "Run-on done: Fans off")]
    [InlineData(EventActions.InterlockTrip, """{"missing":"Fan","off":["Heat"]}""", "Safety shut-off: Heat turned off. Fan must be on first.")]
    [InlineData(EventActions.InterlockTrip, """{"missing":"Fans","off":["Heaters","Lamp"]}""", "Safety shut-off: Heaters and Lamp turned off. Fans must be on first.")]
    [InlineData(EventActions.InterlockTrip, """{"missing":"Fan","off":[]}""", "Safety shut-off: Fan turned on to run on")]
    [InlineData(EventActions.InterlockTrip, """{"missing":"Fans","off":[]}""", "Safety shut-off: Fans turned on to run on")]
    public void Should_ReadRight_ForASingularOrPluralStepName(string action, string details, string text)
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, action, details), "Relay 1", null, "Fan heater");

        Assert.Equal(text, view.Text);
    }

    [Fact]
    public void Should_KeepANamesBraces_OutOfTheLinesPlaceholders()
    {
        var view = TestTexts.Events.For(Entry("john@example.com", EventActions.Rename, """{"from":"{0} {1}","to":"Lamp"}"""), "Lamp", "{0}");

        Assert.Equal("{0} renamed {0} {1} to", view.Text);
        Assert.Equal(("renamed {0} {1} to", "Lamp"), (view.What, view.Target));
    }

    [Fact]
    public void Should_ReadMalformedDetailsWithoutFailing()
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.InterlockTrip, "{not json"), null, null, "Fan heater");

        Assert.Equal("the relay above turned on to run on", view.What);
    }

    // Saved details with values of the wrong type: the rules that went with a turn-off are left out, never a failure.
    [Fact]
    public void Should_ReadWronglyTypedDetailsWithoutFailing()
    {
        var entry = new EventLogEntry
        {
            TimestampUtc = Now,
            Actor = "erik@example.com",
            Action = EventActions.BoardOwnRulesTurnedOff,
            Details = """{"relays":[3],"kinds":["input-linkage"],"also":[{"relay":"5"},7,{"kind":"timer"}]}""",
        };

        var view = TestTexts.Events.For(entry, null, "Erik");

        Assert.Equal("Erik reset relay 3", view.Text);
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
        Assert.Equal(expected, TestTexts.Events.Seconds(seconds));
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
    [InlineData(7200, "2 h")]
    public void Should_CountDownTheTimeLeft(double seconds, string expected)
    {
        Assert.Equal(expected, TestTexts.GroupStatus.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Should_CountTheGroupAsOn_UnlessItIsStopping()
    {
        Assert.True(FanHeater.IsOn(State(1)));
        Assert.False((FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Stopping } }).IsOn(State(1)));
        Assert.True((FanHeater with { Runtime = new GroupRuntime { Phase = GroupPhases.Starting } }).IsOn(State()));
        Assert.False(FanHeater.IsOn(State()));
    }

    private static BoardState State(params int[] on) => new(true, ChannelMask.Of(on), ChannelMask.None, Now);

    private static EventLogEntry Entry(string actor, string action, string? details) =>
        new() { TimestampUtc = Now, GroupId = 1, RelayId = 1, Actor = actor, Action = action, Details = details };
}
