using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class EventTextTests
{
    private const string Relay = "Värmefläkt – Svarven";

    [Theory]
    [InlineData(EventActions.On, "slog på")]
    [InlineData(EventActions.Off, "stängde av")]
    [InlineData(EventActions.Activate, "gjorde reläet aktivt")]
    [InlineData(EventActions.Deactivate, "gjorde reläet inaktivt")]
    [InlineData(EventActions.ScheduleAdd, "lade till ett schema")]
    public void Should_ShowAPersonWithTheirInitial(string action, string what)
    {
        var view = EventText.For(Entry("nils@example.com", action), Relay, "Nils");

        Assert.Equal(new EventView(true, "N", "Nils", what, Relay), view);
    }

    [Fact]
    public void Should_FallBackToTheAddress_WhenThePersonIsUnknown()
    {
        var view = EventText.For(Entry("åsa@example.com", EventActions.On), Relay, personName: null);

        Assert.Equal(("åsa@example.com", "Å"), (view.Who, view.Badge));
    }

    [Theory]
    [InlineData(EventActors.Schedule, EventActions.On, "Schema", "slog på")]
    [InlineData(EventActors.Schedule, EventActions.Off, "Schema", "stängde av")]
    [InlineData(EventActors.External, EventActions.On, "Utanför appen", "slogs på")]
    [InlineData(EventActors.External, EventActions.Off, "Utanför appen", "stängdes av")]
    public void Should_ShowAutomaticEventsWithA(string actor, string action, string who, string what)
    {
        var view = EventText.For(Entry(actor, action), Relay, personName: null);

        Assert.Equal(new EventView(false, "A", who, what, Relay), view);
    }

    [Theory]
    [InlineData("""{"afterMinutes":120}""", "stängde av efter 2 h")]
    [InlineData("""{"afterMinutes":90}""", "stängde av efter 90 min")]
    [InlineData(null, "stängde av efter en stund")]
    [InlineData("not json", "stängde av efter en stund")]
    public void Should_SayHowLongAutoOffWaited(string? details, string what)
    {
        var view = EventText.For(Entry(EventActors.AutoOff, EventActions.Off, details), Relay, personName: null);

        Assert.Equal(("Auto-av", what), (view.Who, view.What));
    }

    [Fact]
    public void Should_ShowTheOldAndNewName_WhenRenamed()
    {
        var entry = Entry("erik@example.com", EventActions.Rename, """{"from":"Relä 4","to":"Ventilation – Lackrummet"}""");

        var view = EventText.For(entry, "Ventilation – Lackrummet", "Erik");

        Assert.Equal(("döpte om Relä 4 till", "Ventilation – Lackrummet"), (view.What, view.Target));
    }

    [Theory]
    [InlineData(EventActions.ConnectionLost, null, "Kortet", "tappade kontakten med appen", "A")]
    [InlineData(EventActions.ConnectionRestored, """{"afterSeconds":40}""", "Kortet", "fick kontakt igen", "A")]
    [InlineData(EventActions.BoardTimersDisabled, """{"mask":255}""", "Kortet", "fick sina egna timers avstängda", "A")]
    [InlineData(EventActions.InputChanged, """{"on":true}""", "Ingången", "slogs på", "I")]
    [InlineData(EventActions.InputChanged, """{"on":false}""", "Ingången", "slogs av", "I")]
    public void Should_DescribeBoardEvents(string action, string? details, string who, string what, string badge)
    {
        var view = EventText.For(Entry(EventActors.System, action, details, relayId: null), relayName: null, personName: null);

        Assert.Equal(new EventView(false, badge, who, what, null), view);
    }

    [Theory]
    [InlineData(EventActors.Schedule, "schema")]
    [InlineData(EventActors.AutoOff, "auto-av")]
    [InlineData(EventActors.External, "utanför appen")]
    public void Should_GiveAShortNameForTheStatusLine(string actor, string expected)
    {
        Assert.Equal(expected, EventText.ShortWho(actor, personName: null));
    }

    private static EventLogEntry Entry(string actor, string action, string? details = null, int? relayId = 1)
    {
        return new EventLogEntry { Actor = actor, Action = action, Details = details, RelayId = relayId };
    }
}
