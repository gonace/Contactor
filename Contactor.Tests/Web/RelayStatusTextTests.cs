using Contactor.Boards;
using Contactor.Web.Features.Relays;

namespace Contactor.Tests.Web;

public sealed class RelayStatusTextTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 15, 30, 0);

    [Theory]
    [InlineData(2026, 11, 1, "1 Nov 08:00")]
    [InlineData(2026, 3, 29, "29 Mar 08:00")]
    [InlineData(2027, 1, 2, "2 Jan 08:00")]
    [InlineData(2026, 9, 3, "3 Sep 08:00")]
    [InlineData(2026, 5, 31, "31 May 08:00")]
    public void Should_GiveTheDate_WhenNotNearToday(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, TestTexts.RelayStatus.When(new DateTime(year, month, day, 8, 0, 0), Now));
    }

    // A press the board didn't confirm is settled once the board, online, shows the relay as the person wanted it.
    [Fact]
    public void Should_SettleAnUnconfirmedPress_OnlyWhenTheBoardShowsWhatWasWanted()
    {
        var on = new UnconfirmedPress(13, 3, On: true);
        var off = new UnconfirmedPress(13, 3, On: false);
        var relay3On = new BoardState(true, ChannelMask.Of(3), ChannelMask.None, DateTimeOffset.UnixEpoch);
        var relay3Off = new BoardState(true, ChannelMask.None, ChannelMask.None, DateTimeOffset.UnixEpoch);

        Assert.True(on.IsSettledBy(relay3On));
        Assert.False(on.IsSettledBy(relay3Off));
        Assert.True(off.IsSettledBy(relay3Off));
        Assert.False(on.IsSettledBy(relay3On with { IsConnected = false }));
    }
}
