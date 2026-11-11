using Contactor.Web.Features.Relays;

namespace Contactor.Tests.Web;

public sealed class RelayStatusTextTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 15, 30, 0);

    [Fact]
    public void Should_SayOff_WhenOff()
    {
        Assert.Equal("Off", RelayStatusText.For(false, Now.AddHours(-1), "Jane", Now.AddHours(1), Now));
    }

    [Fact]
    public void Should_SayWhenAndWho_WhenTurnedOff()
    {
        var text = RelayStatusText.For(false, null, null, null, Now, new DateTime(2026, 11, 3, 18, 0, 0), "Alex");

        Assert.Equal("Off since yesterday 18:00 · Alex", text);
    }

    [Fact]
    public void Should_SayWhen_WhenWhoIsUnknown()
    {
        Assert.Equal("Off since 12:15", RelayStatusText.For(false, null, null, null, Now, new DateTime(2026, 11, 4, 12, 15, 0), null));
    }

    [Fact]
    public void Should_IgnoreTheOffTime_WhenOn()
    {
        var text = RelayStatusText.For(true, null, null, null, Now, new DateTime(2026, 11, 4, 12, 15, 0), "schedule");

        Assert.Equal("On", text);
    }

    [Fact]
    public void Should_SayWhenWhoAndUntil_WhenTurnedOnInTheApp()
    {
        var text = RelayStatusText.For(true, new DateTime(2026, 11, 4, 14, 2, 0), "Jane", new DateTime(2026, 11, 4, 16, 2, 0), Now);

        Assert.Equal("On since 14:02 · Jane · off 16:02", text);
    }

    [Fact]
    public void Should_LeaveOutAutoOff_WhenItNeverTurnsOff()
    {
        Assert.Equal("On since 14:02 · you", RelayStatusText.For(true, new DateTime(2026, 11, 4, 14, 2, 0), "you", null, Now));
    }

    [Fact]
    public void Should_SayOnlyOn_WhenTurnedOnOutsideTheApp()
    {
        Assert.Equal("On", RelayStatusText.For(true, null, null, null, Now));
    }

    [Fact]
    public void Should_NameTheDay_AcrossMidnight()
    {
        var text = RelayStatusText.For(
            true,
            new DateTime(2026, 11, 3, 23, 10, 0),
            "Alex",
            new DateTime(2026, 11, 5, 1, 10, 0),
            Now);

        Assert.Equal("On since yesterday 23:10 · Alex · off tomorrow 01:10", text);
    }

    [Theory]
    [InlineData(2026, 11, 1, "1 Nov 08:00")]
    [InlineData(2026, 3, 29, "29 Mar 08:00")]
    [InlineData(2027, 1, 2, "2 Jan 08:00")]
    public void Should_GiveTheDate_WhenNotNearToday(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, RelayStatusText.When(new DateTime(year, month, day, 8, 0, 0), Now));
    }

    [Fact]
    public void Should_WriteWholeSentences_ForTheRelayScreen()
    {
        Assert.Equal(
            "On since 14:02, by Jane. Turns off 16:02.",
            RelayStatusText.Sentence(true, new DateTime(2026, 11, 4, 14, 2, 0), "Jane", new DateTime(2026, 11, 4, 16, 2, 0), Now));
        Assert.Equal("On since 14:02, by you.", RelayStatusText.Sentence(true, new DateTime(2026, 11, 4, 14, 2, 0), "you", null, Now));
        Assert.Equal("On right now.", RelayStatusText.Sentence(true, null, null, null, Now));
        Assert.Equal("Off right now.", RelayStatusText.Sentence(false, null, null, null, Now));
    }

    [Fact]
    public void Should_SayWhenTheScheduleTurnsItOn_BeforeWhoTurnedItOff()
    {
        var text = RelayStatusText.For(false, null, null, null, Now, new DateTime(2026, 11, 3, 18, 0, 0), "Alex", new DateTime(2026, 11, 5, 6, 30, 0));

        Assert.Equal("Schedule: on tomorrow 06:30", text);
    }

    [Fact]
    public void Should_SayTheScheduleTurnedItOn()
    {
        Assert.Equal(
            "On since 06:30 · schedule · off 08:00",
            RelayStatusText.For(true, new DateTime(2026, 11, 4, 6, 30, 0), "schedule", new DateTime(2026, 11, 4, 8, 0, 0), Now));
        Assert.Equal(
            "On since 06:30, by a schedule. Turns off 08:00.",
            RelayStatusText.Sentence(true, new DateTime(2026, 11, 4, 6, 30, 0), "schedule", new DateTime(2026, 11, 4, 8, 0, 0), Now));
    }
}
