using Contactor.Boards;
using Contactor.Web.Features.Relays;

namespace Contactor.Tests.Web;

/// <summary>A board's "N of M on" and its lamps count the same relays and groups (Issue #423).</summary>
public sealed class BoardCountsTests
{
    [Fact]
    public void Should_GiveALampForEachRelayAndGroupItCounts_InChannelOrder()
    {
        // Board 1: relays 2 and 6 in no group, a group of 4 and 5, and a group with no relays; board 2's relay 1.
        var counts = new BoardCounts([(1, 6), (1, 2), (2, 1)], [(1, ChannelMask.Of([4, 5])), (1, ChannelMask.None)]);
        var state = new BoardState(true, ChannelMask.Of([1, 5, 6, 7]), ChannelMask.None, DateTimeOffset.UnixEpoch);

        Assert.Equal([false, true, true, false], counts.Lamps(1, state));
        Assert.Equal((2, 4), counts.Of(1, state));
        Assert.Equal([true], counts.Lamps(2, state));
        Assert.Empty(counts.Lamps(3, state));
        Assert.Equal((0, 0), counts.Of(3, state));
    }
}
