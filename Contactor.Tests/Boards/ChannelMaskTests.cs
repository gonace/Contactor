using Contactor.Boards;

namespace Contactor.Tests.Boards;

public sealed class ChannelMaskTests
{
    [Fact]
    public void Should_NumberChannelsFromOne_UpToSixtyFour()
    {
        Assert.Equal(1UL, ChannelMask.Of(1).Bits);
        Assert.Equal(1UL << 63, ChannelMask.Of(64).Bits);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMask.Of(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMask.Of(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMask.Of(-1));
    }

    [Fact]
    public void Should_ListItsChannels_LowestFirst()
    {
        var mask = ChannelMask.Of([8, 1, 3, 64, 3]);

        Assert.Equal([1, 3, 8, 64], mask.Channels);
        Assert.Equal(4, mask.Count);
        Assert.Equal("1,3,8,64", mask.ToString());
        Assert.Equal("none", ChannelMask.None.ToString());
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(1, 1UL)]
    [InlineData(8, 0xFFUL)]
    [InlineData(64, ulong.MaxValue)]
    public void Should_TakeTheFirstChannels(int count, ulong bits)
    {
        Assert.Equal(bits, ChannelMask.FirstN(count).Bits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public void Should_RefuseImpossibleCounts(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMask.FirstN(count));
    }

    [Fact]
    public void Should_AnswerWhetherAChannelIsIn_EvenOutOfRange()
    {
        var mask = ChannelMask.Of([2, 5]);

        Assert.True(mask.Contains(2));
        Assert.False(mask.Contains(3));
        Assert.False(mask.Contains(0));
        Assert.False(mask.Contains(65));
        Assert.False(mask.Contains(int.MinValue));
    }

    [Fact]
    public void Should_CombineSets()
    {
        var a = ChannelMask.Of([1, 2, 3]);
        var b = ChannelMask.Of([3, 4]);

        Assert.Equal(ChannelMask.Of([1, 2, 3, 4]), a | b);
        Assert.Equal(ChannelMask.Of(3), a & b);
        Assert.Equal(ChannelMask.Of([1, 2]), a.Except(b));
        Assert.True(a.Overlaps(b));
        Assert.False(a.Overlaps(ChannelMask.Of(8)));
        Assert.True(a.ContainsAll(ChannelMask.Of([1, 3])));
        Assert.False(a.ContainsAll(b));
        Assert.True(a.ContainsAll(ChannelMask.None));
        Assert.True(ChannelMask.None.IsEmpty);
    }
}
