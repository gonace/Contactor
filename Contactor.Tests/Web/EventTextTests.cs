using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class EventTextTests
{
    private const string Relay = "Fan heater – Lathe";

    [Theory]
    [InlineData(EventActions.On, "turned on")]
    [InlineData(EventActions.Off, "turned off")]
    [InlineData(EventActions.Activate, "made the relay active")]
    [InlineData(EventActions.Deactivate, "made the relay inactive")]
    [InlineData(EventActions.ScheduleAdd, "added a schedule")]
    public void Should_ShowAPersonWithTheirInitial(string action, string what)
    {
        var view = TestTexts.Events.For(Entry("jane@example.com", action), Relay, "Jane");

        Assert.Equal(new EventView(true, "J", "Jane", "{0} " + what, Relay), view);
    }

    // A pulse: one event, with how long.
    [Theory]
    [InlineData("""{"milliseconds":2000}""", "tried for 2 s")]
    [InlineData("""{"milliseconds":500}""", "tried for 0.5 s")]
    [InlineData("""{"milliseconds":90000}""", "tried for 1 min 30 s")]
    [InlineData(null, "tried for a moment")]
    public void Should_SayHowLongAPulseWas(string? details, string what)
    {
        var entry = Entry("jane@example.com", EventActions.Pulse);
        entry.Details = details;

        Assert.Equal(new EventView(true, "J", "Jane", "{0} " + what, Relay), TestTexts.Events.For(entry, Relay, "Jane"));
    }

    // "Off since" after a pulse: when the board turned the relay off, at the pulse's end.
    [Fact]
    public void Should_EndAPulse_AfterItsLength()
    {
        var at = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var entry = Entry("jane@example.com", EventActions.Pulse, """{"milliseconds":2000}""");
        entry.TimestampUtc = at;

        Assert.Equal(at.AddSeconds(2), EventText.PulseEndUtc(entry));
        entry.Details = null;
        Assert.Equal(at, EventText.PulseEndUtc(entry));
    }

    [Fact]
    public void Should_FallBackToTheAddress_WhenThePersonIsUnknown()
    {
        var view = TestTexts.Events.For(Entry("åsa@example.com", EventActions.On), Relay, personName: null);

        Assert.Equal(("åsa@example.com", "Å"), (view.Who, view.Badge));
    }

    [Theory]
    [InlineData(EventActors.Schedule, EventActions.On, "Schedule", "turned on")]
    [InlineData(EventActors.Schedule, EventActions.Off, "Schedule", "turned off")]
    [InlineData(EventActors.External, EventActions.On, "Outside the app", "turned on")]
    [InlineData(EventActors.External, EventActions.Off, "Outside the app", "turned off")]
    public void Should_ShowAutomaticEventsWithA(string actor, string action, string who, string what)
    {
        var view = TestTexts.Events.For(Entry(actor, action), Relay, personName: null);

        Assert.Equal(new EventView(false, "A", who, "{0} " + what, Relay), view);
    }

    [Theory]
    [InlineData("""{"afterMinutes":120}""", "turned off after 2 h")]
    [InlineData("""{"afterMinutes":90}""", "turned off after 90 min")]
    [InlineData(null, "turned off after a while")]
    [InlineData("not json", "turned off after a while")]
    public void Should_SayHowLongAutoOffWaited(string? details, string what)
    {
        var view = TestTexts.Events.For(Entry(EventActors.AutoOff, EventActions.Off, details), Relay, personName: null);

        Assert.Equal(("Auto-off", what), (view.Who, view.What));
    }

    [Fact]
    public void Should_ShowTheOldAndNewName_WhenRenamed()
    {
        var entry = Entry("sam@example.com", EventActions.Rename, """{"from":"Relay 4","to":"Ventilation – Paint shop"}""");

        var view = TestTexts.Events.For(entry, "Ventilation – Paint shop", "Sam");

        Assert.Equal(("renamed Relay 4 to", "Ventilation – Paint shop"), (view.What, view.Target));
    }

    [Theory]
    [InlineData("""{"from":"Barn","to":"Garage"}""", "renamed the relay board from Barn to Garage")]
    [InlineData("""{"to":"Garage"}""", "renamed the relay board to Garage")]
    [InlineData(null, "renamed the relay board to ?")]
    public void Should_ShowTheBoardsOldAndNewName_WhenTheBoardIsRenamed(string? details, string what)
    {
        var entry = Entry("sam@example.com", EventActions.BoardRenamed, details, relayId: null);

        var view = TestTexts.Events.For(entry, null, "Sam");

        Assert.Equal(("Sam", what, (string?)null), (view.Who, view.What, view.Target));
    }

    [Theory]
    [InlineData(true, "Door:", "Closed")]
    [InlineData(false, "Door:", "Open")]
    public void Should_DescribeAnInputByItsName(bool on, string who, string what)
    {
        var entry = Entry(EventActors.External, EventActions.InputChanged, on ? """{"on":true}""" : """{"on":false}""", relayId: null);
        entry.InputId = 2;

        var view = TestTexts.Events.For(entry, relayName: null, personName: null, inputName: "Door");

        Assert.Equal(new EventView(false, "I", who, "{0} " + what, null), view);
    }

    // A change the app only noticed after a gap: its time is when it was noticed, so it says the real time is unknown.
    [Theory]
    [InlineData("""{"on":false,"noticed":true}""", true)]
    [InlineData("""{"on":false}""", false)]
    public void Should_SayTheTimeIsUnknown_ForAnInputChangeNoticedAfterAGap(string details, bool unknown)
    {
        var entry = Entry(EventActors.External, EventActions.InputChanged, details, relayId: null);
        entry.InputId = 2;

        var view = TestTexts.Events.For(entry, relayName: null, personName: null, inputName: "Door");

        Assert.Equal(("Door:", "Open", unknown), (view.Who, view.What, view.TimeUnknown));
    }

    // An input no longer in the app (a removed board's) has no name to show, and its id is not the number the screens
    // show. The relay's status line keeps its own short words.
    [Fact]
    public void Should_NameAnInputPlainly_WithoutItsName()
    {
        var entry = Entry(EventActors.External, EventActions.InputChanged, """{"on":true}""", relayId: null);
        entry.InputId = 201;

        Assert.Equal(new EventView(false, "I", "A removed input:", "{0} Closed", null), TestTexts.Events.For(entry, relayName: null, personName: null));
        var on = Entry(EventActors.ForInput(201), EventActions.On, details: null, relayId: 4);
        Assert.Equal(("A removed input", "An input"), (TestTexts.Events.For(on, "Lighting", personName: null).Who, TestTexts.Events.ShortWho(on.Actor, personName: null)));
    }

    // A relay or group id with no name left (a removed board's) shows plain words, or the name the event saved; one
    // that still resolves shows its name.
    [Fact]
    public void Should_NameARemovedRelayOrGroupPlainly()
    {
        Assert.Equal("a removed relay", TestTexts.Events.For(Entry("jane@example.com", EventActions.On, relayId: 301), relayName: null, "Jane").Target);
        Assert.Equal(Relay, TestTexts.Events.For(Entry("jane@example.com", EventActions.On, relayId: 301), Relay, "Jane").Target);
        Assert.Null(TestTexts.Events.For(Entry(EventActors.System, EventActions.ConnectionLost, relayId: null), relayName: null, personName: null).Target);

        var group = Entry("jane@example.com", EventActions.GroupOff, relayId: null);
        group.GroupId = 40;
        Assert.Equal("a removed group", TestTexts.Events.For(group, relayName: null, "Jane").Target);
        Assert.Equal("Fan heater", TestTexts.Events.For(group, relayName: null, "Jane", groupName: "Fan heater").Target);
        group.Details = """{"name":"Heater"}""";
        Assert.Equal("Heater", TestTexts.Events.For(group, relayName: null, "Jane").Target);

        var runOn = Entry(EventActors.System, EventActions.RunOnEnd, relayId: 301);
        runOn.GroupId = 40;
        Assert.Equal("{0} a removed relay off", TestTexts.Events.For(runOn, relayName: null, personName: null).Line);
    }

    // A removed board is named from the event: its row is gone.
    [Theory]
    [InlineData("""{"name":"Garage","model":"Velleman VM204"}""", "removed relay board Garage (Velleman VM204)")]
    [InlineData("""{"name":"Garage"}""", "removed relay board Garage")]
    [InlineData("""{"name":"","model":"Velleman VM204"}""", "removed relay board Velleman VM204")]
    [InlineData(null, "removed relay board ?")]
    [InlineData("not json", "removed relay board ?")]
    public void Should_SayWhoRemovedABoard(string? details, string what)
    {
        var view = TestTexts.Events.For(Entry("john@example.com", EventActions.BoardRemoved, details, relayId: null), relayName: null, "John");

        Assert.Equal(("John", what, null, "J"), (view.Who, view.What, view.Target, view.Badge));
        Assert.Equal("John " + what, view.Text);
    }

    [Theory]
    [InlineData(EventActions.ConnectionLost, null, "The board", "lost contact with the app", "A")]
    [InlineData(EventActions.ConnectionRestored, """{"afterSeconds":40}""", "The board", "reconnected", "A")]
    [InlineData(EventActions.BoardTimersDisabled, """{"mask":255}""", "The board", "had its own timers turned off", "A")]
    [InlineData(EventActions.BoardRelayModesReset, """{"modes":[0,0,1,0,0,0,0,0]}""", "The board", "had its relays set to normal mode, so its inputs don't switch them", "A")]
    public void Should_DescribeBoardEvents(string action, string? details, string who, string what, string badge)
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, action, details, relayId: null), relayName: null, personName: null);

        Assert.Equal(new EventView(false, badge, who, "{0} " + what, null), view);
    }

    [Fact]
    public void Should_ShowInRed_ABoardWhoseRelayModesCouldntBeReset()
    {
        var entry = Entry(EventActors.System, EventActions.BoardRelayModesNotReset, """{"reason":"The board refused it: it failed (code 4)."}""", relayId: null);

        var view = TestTexts.Events.For(entry, relayName: null, personName: null);

        Assert.Equal(new EventView(false, "A", "The board", "{0} couldn't have its relays set to normal mode, so its inputs may switch them", null, IsAlert: true), view);
    }

    [Theory]
    [InlineData(EventActors.Schedule, "schedule")]
    [InlineData(EventActors.AutoOff, "auto-off")]
    [InlineData(EventActors.External, "outside the app")]
    public void Should_GiveAShortNameForTheStatusLine(string actor, string expected)
    {
        Assert.Equal(expected, TestTexts.Events.ShortWho(actor, personName: null));
    }

    private static EventLogEntry Entry(string actor, string action, string? details = null, int? relayId = 1)
    {
        return new EventLogEntry { Actor = actor, Action = action, Details = details, RelayId = relayId };
    }
}
