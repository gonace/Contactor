using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>A board's analog inputs: what a count means (<see cref="AnalogAbility"/>) and the readings (<see cref="AnalogReadings"/>).</summary>
public sealed class AnalogTests
{
    [Fact]
    public void Should_CompareReadingsByValue()
    {
        var readings = new AnalogReadings([1, null, 1023]);

        Assert.Equal(new AnalogReadings([1, null, 1023]), readings);
        Assert.True(new AnalogReadings([1, null, 1023]) == readings);
        Assert.Equal(new AnalogReadings([1, null, 1023]).GetHashCode(), readings.GetHashCode());
        Assert.NotEqual(new AnalogReadings([1, 0, 1023]), readings);
        Assert.NotEqual(new AnalogReadings([1, null]), readings);
        Assert.False(readings.Equals(null));
        Assert.Equal(new BoardState(true, ChannelMask.None, ChannelMask.None, null, Analog: new AnalogReadings([5])), new BoardState(true, ChannelMask.None, ChannelMask.None, null, Analog: new AnalogReadings([5])));
    }

    [Fact]
    public void Should_KeepItsOwnCopy_AndGiveNoReadingForAnInputItDoesntHave()
    {
        int?[] counts = [7, null];
        var readings = new AnalogReadings(counts);
        counts[0] = 8;

        Assert.Equal(2, readings.Count);
        Assert.Equal(7, readings[0]);
        Assert.Equal(7, readings.Input(1));
        Assert.Null(readings.Input(2));
        Assert.Null(readings.Input(0));
        Assert.Null(readings.Input(3));
        Assert.Equal("[7, none]", readings.ToString());
    }

    [Fact]
    public void Should_CheckAReading_AgainstTheConvertersRange()
    {
        var analog = new AnalogAbility(8, 1023, 3.3m);

        Assert.Equal(0, analog.Check(0));
        Assert.Equal(1023, analog.Check(1023));
        Assert.Null(analog.Check(1024));
        Assert.Null(analog.Check(-1));
        Assert.Null(analog.Check(null));
        Assert.Equal(3.3m, analog.Volts(1023));
        Assert.True(analog.IsFullScale(1023));
    }

    [Fact]
    public void Should_RefuseAnAbilityThatCantWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogAbility(0, 1023, 5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogAbility(1, 0, 5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogAbility(1, 1023, 0m));
        Assert.Equal(0, new BoardCapabilities(1, 0, ReportsChanges: false).AnalogInputCount);
    }
}
