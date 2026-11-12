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
        var view = EventText.For(Entry("jane@example.com", action), Relay, "Jane");

        Assert.Equal(new EventView(true, "J", "Jane", what, Relay), view);
    }

    [Fact]
    public void Should_FallBackToTheAddress_WhenThePersonIsUnknown()
    {
        var view = EventText.For(Entry("åsa@example.com", EventActions.On), Relay, personName: null);

        Assert.Equal(("åsa@example.com", "Å"), (view.Who, view.Badge));
    }

    [Theory]
    [InlineData(EventActors.Schedule, EventActions.On, "Schedule", "turned on")]
    [InlineData(EventActors.Schedule, EventActions.Off, "Schedule", "turned off")]
    [InlineData(EventActors.External, EventActions.On, "Outside the app", "turned on")]
    [InlineData(EventActors.External, EventActions.Off, "Outside the app", "turned off")]
    public void Should_ShowAutomaticEventsWithA(string actor, string action, string who, string what)
    {
        var view = EventText.For(Entry(actor, action), Relay, personName: null);

        Assert.Equal(new EventView(false, "A", who, what, Relay), view);
    }

    [Theory]
    [InlineData("""{"afterMinutes":120}""", "turned off after 2 h")]
    [InlineData("""{"afterMinutes":90}""", "turned off after 90 min")]
    [InlineData(null, "turned off after a while")]
    [InlineData("not json", "turned off after a while")]
    public void Should_SayHowLongAutoOffWaited(string? details, string what)
    {
        var view = EventText.For(Entry(EventActors.AutoOff, EventActions.Off, details), Relay, personName: null);

        Assert.Equal(("Auto-off", what), (view.Who, view.What));
    }

    [Fact]
    public void Should_ShowTheOldAndNewName_WhenRenamed()
    {
        var entry = Entry("sam@example.com", EventActions.Rename, """{"from":"Relay 4","to":"Ventilation – Paint shop"}""");

        var view = EventText.For(entry, "Ventilation – Paint shop", "Sam");

        Assert.Equal(("renamed Relay 4 to", "Ventilation – Paint shop"), (view.What, view.Target));
    }

    [Theory]
    [InlineData(true, "Door:", "Closed")]
    [InlineData(false, "Door:", "Open")]
    public void Should_DescribeAnInputInItsOwnWords(bool on, string who, string what)
    {
        var entry = Entry(EventActors.External, EventActions.InputChanged, on ? """{"on":true}""" : """{"on":false}""", relayId: null);
        entry.InputId = 2;

        var view = EventText.For(entry, relayName: null, personName: null, input: new InputWords("Door", "Closed", "Open"));

        Assert.Equal(new EventView(false, "I", who, what, null), view);
    }

    // An input event from before inputs had names, or of an input no longer on the board.
    [Fact]
    public void Should_NameAnInputByItsNumber_WithoutItsWords()
    {
        var entry = Entry(EventActors.External, EventActions.InputChanged, """{"on":true}""", relayId: null);

        Assert.Equal(new EventView(false, "I", "Input 1:", "Closed", null), EventText.For(entry, relayName: null, personName: null));
        entry.InputId = 3;
        Assert.Equal("Input 3:", EventText.For(entry, relayName: null, personName: null).Who);
    }

    [Theory]
    [InlineData(EventActions.ConnectionLost, null, "The board", "lost contact with the app", "A")]
    [InlineData(EventActions.ConnectionRestored, """{"afterSeconds":40}""", "The board", "reconnected", "A")]
    [InlineData(EventActions.BoardTimersDisabled, """{"mask":255}""", "The board", "had its own timers turned off", "A")]
    public void Should_DescribeBoardEvents(string action, string? details, string who, string what, string badge)
    {
        var view = EventText.For(Entry(EventActors.System, action, details, relayId: null), relayName: null, personName: null);

        Assert.Equal(new EventView(false, badge, who, what, null), view);
    }

    [Theory]
    [InlineData(EventActors.Schedule, "schedule")]
    [InlineData(EventActors.AutoOff, "auto-off")]
    [InlineData(EventActors.External, "outside the app")]
    public void Should_GiveAShortNameForTheStatusLine(string actor, string expected)
    {
        Assert.Equal(expected, EventText.ShortWho(actor, personName: null));
    }

    private static EventLogEntry Entry(string actor, string action, string? details = null, int? relayId = 1)
    {
        return new EventLogEntry { Actor = actor, Action = action, Details = details, RelayId = relayId };
    }
}
