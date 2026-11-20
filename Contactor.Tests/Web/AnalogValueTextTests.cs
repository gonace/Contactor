using Contactor.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>An analog input's value: never a value the board didn't measure.</summary>
public sealed class AnalogValueTextTests
{
    // A 10-bit converter for 0–5 V, e.g. the ETH8020-B's.
    private static readonly AnalogAbility TenBitFiveVolts = new(2, 1023, 5m);

    // 0 V = −20 °C, 5 V = 80 °C.
    private static readonly AnalogScale Celsius = new(AnalogUnits.Celsius, "", 0m, -20m, 5m, 80m);

    private static readonly BoardState Connected = new(true, ChannelMask.None, ChannelMask.None, DateTimeOffset.UnixEpoch);

    // The top of the board's range may stand for any higher voltage: no value, never 80 °C.
    [Fact]
    public void Should_ShowNoValue_AtTheTopOfTheBoardsRange()
    {
        Assert.Equal(new AnalogValue(AnalogValueState.AboveRange, "–", "", null, ""), Show(1023));
        Assert.Equal(AnalogValueState.Value, Show(1022).State);
    }

    // No reading, or one outside the board's range: no value.
    [Fact]
    public void Should_ShowNoValue_WithoutAReading()
    {
        Assert.Equal(new AnalogValue(AnalogValueState.NoReading, "–", "", null, ""), Show(null));
        Assert.Equal(AnalogValueState.NoReading, Show(1024).State);
        Assert.Equal(AnalogValueState.NoReading, Show(-1).State);
    }

    // A reading from before the contact was lost may be stale, and the board may not have read its inputs yet: no value.
    [Fact]
    public void Should_ShowNoValue_WithoutContact_OrBeforeTheBoardsFirstReading()
    {
        var lost = Connected with { IsConnected = false, Analog = new AnalogReadings([430]) };

        Assert.Equal(new AnalogValue(AnalogValueState.NoContact, "–", "", null, ""), AnalogValueText.Show(Celsius, TenBitFiveVolts, lost, 1));
        Assert.Equal(AnalogValueState.Waiting, AnalogValueText.Show(Celsius, TenBitFiveVolts, BoardState.Initial, 1).State);
        Assert.Equal(AnalogValueState.Waiting, AnalogValueText.Show(Celsius, TenBitFiveVolts, Connected, 1).State);
    }

    private static AnalogValue Show(int? counts)
    {
        return AnalogValueText.Show(Celsius, TenBitFiveVolts, Connected with { Analog = new AnalogReadings([counts]) }, 1);
    }
}
