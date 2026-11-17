using Contactor.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>An analog input's value as text: the scale, the decimals the board can measure, the unit and the states.</summary>
public sealed class AnalogValueTextTests
{
    // A 10-bit converter for 0–5 V, e.g. the ETH8020-B's, and the VM204's 0–6.6 V.
    private static readonly AnalogAbility TenBitFiveVolts = new(2, 1023, 5m);
    private static readonly AnalogAbility Vm204 = new(1, 1023, 6.6m);

    // 0 V = −20 °C, 5 V = 80 °C.
    private static readonly AnalogScale Celsius = new(AnalogUnits.Celsius, "", 0m, -20m, 5m, 80m);

    private static readonly BoardState Connected = new(true, ChannelMask.None, ChannelMask.None, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Should_ShowAScaledTenBitInput_WithOneDecimal_AndVoltsWithTwo()
    {
        Assert.Equal(1, AnalogValueText.Decimals(TenBitFiveVolts.VoltsPerCount, Celsius));
        Assert.Equal(2, AnalogValueText.Decimals(TenBitFiveVolts.VoltsPerCount, AnalogScale.None));
        Assert.Equal(2, AnalogValueText.Decimals(Vm204.VoltsPerCount, AnalogScale.None));
    }

    [Theory]
    [InlineData(4095, 10, 0, 100, 1)] // 12 bits, 0–10 V as 0–100 %: a step of 0.024 %
    [InlineData(1023, 5, 0, 10000, 0)] // a step of 9.8: no decimals, never fewer than none
    [InlineData(1023, 3.3, 0, 0.01, 3)] // a step of 0.00003: at most 3
    [InlineData(1023, 5, 80, -20, 1)] // a falling scale: the step's size counts
    [InlineData(10, 1, 0, 1, 1)] // a step of exactly 0.1 V: 1, not 0
    [InlineData(1023, 5, 7, 7, 3)] // one value at both points: no step at all
    public void Should_GiveTheDecimals_TheBoardCanMeasure(int maxCounts, double fullScale, double value1, double value2, int decimals)
    {
        var ability = new AnalogAbility(1, maxCounts, (decimal)fullScale);
        var scale = new AnalogScale(AnalogUnits.Other, "x", 0m, (decimal)value1, (decimal)fullScale, (decimal)value2);

        Assert.Equal(decimals, AnalogValueText.Decimals(ability.VoltsPerCount, scale));
    }

    [Theory]
    [InlineData(0, -20)]
    [InlineData(5, 80)]
    [InlineData(2.5, 30)]
    [InlineData(6, 100)] // past the second point, on the same line
    public void Should_ScaleOnTheLineThroughBothPoints(double volts, double value)
    {
        Assert.Equal((decimal)value, AnalogValueText.Scaled((decimal)volts, Celsius));
    }

    [Fact]
    public void Should_ShowVolts_WithoutAScale()
    {
        Assert.Equal(2.08m, AnalogValueText.Scaled(2.08m, AnalogScale.None));

        // Two points at one voltage are no scale: never a division by zero.
        Assert.Equal(2.08m, AnalogValueText.Scaled(2.08m, new AnalogScale(AnalogUnits.Volts, "", 1m, 0m, 1m, 10m)));
    }

    [Theory]
    [InlineData(AnalogUnits.None, "", "V")]
    [InlineData(AnalogUnits.Volts, "", "V")]
    [InlineData(AnalogUnits.Celsius, "", "°C")]
    [InlineData(AnalogUnits.Percent, "", "%")]
    [InlineData(AnalogUnits.Bar, "", "bar")]
    [InlineData(AnalogUnits.Other, "kPa", "kPa")]
    public void Should_NameTheUnit(string unit, string unitText, string shown)
    {
        Assert.Equal(shown, AnalogValueText.Unit(new AnalogScale(unit, unitText, null, null, null, null)));
    }

    [Fact]
    public void Should_ShowTheValue_ItsUnit_AndWhatTheBoardReads()
    {
        // 422 counts: 2.0626 V, 21.25 °C.
        var value = AnalogValueText.Show(Celsius, TenBitFiveVolts, Readings(422, 0), 1);

        Assert.Equal(new AnalogValue(AnalogValueState.Value, "21.3", "°C", 422 * 5m / 1023, "2.06"), value);
        Assert.Equal("0.00", AnalogValueText.Show(AnalogScale.None, TenBitFiveVolts, Readings(422, 0), 2).Text);
    }

    [Fact]
    public void Should_UseTheCurrentCulture()
    {
        using (TestCulture.Formats("sv-SE"))
        {
            var value = AnalogValueText.Show(Celsius, TenBitFiveVolts, Readings(422), 1);

            Assert.Equal(("21,3", "2,06"), (value.Text, value.VoltsText));
            Assert.Equal("−5,0", AnalogValueText.Number(-5m, 1));
        }

        Assert.Equal("1,234.5", AnalogValueText.Number(1234.5m, 1));
    }

    // −0.0049 rounds to zero: shown as 0.00, never as "-0.00".
    [Fact]
    public void Should_ShowZero_WithoutASign()
    {
        var scale = new AnalogScale(AnalogUnits.Other, "x", 0m, -1m, 5m, 1m);

        Assert.Equal("0.00", AnalogValueText.Show(scale, TenBitFiveVolts, Readings(509), 1).Text);
        Assert.Equal("0", AnalogValueText.Number(-0.4m, 0));
    }

    [Fact]
    public void Should_WaitForTheBoard_ThenShowNoContact_NoReading_AndAboveTheRange()
    {
        Assert.Equal(AnalogValueState.Waiting, State(BoardState.Initial));
        Assert.Equal(AnalogValueState.Waiting, State(Connected));
        Assert.Equal(AnalogValueState.Waiting, AnalogValueText.Show(Celsius, null, Readings(422), 1).State);
        Assert.Equal(AnalogValueState.NoContact, State(Readings(422) with { IsConnected = false }));
        Assert.Equal(AnalogValueState.NoReading, State(Readings([null])));
        Assert.Equal(AnalogValueState.NoReading, State(Readings(1024)));
        Assert.Equal(AnalogValueState.NoReading, State(Readings(-1)));
        Assert.Equal(AnalogValueState.NoReading, AnalogValueText.Show(Celsius, TenBitFiveVolts, Readings(422), 2).State);
        Assert.Equal(AnalogValueState.AboveRange, State(Readings(1023)));
        Assert.Equal(AnalogValueState.Value, State(Readings(0)));

        var none = AnalogValueText.Show(Celsius, TenBitFiveVolts, Readings(1023), 1);
        Assert.Equal(new AnalogValue(AnalogValueState.AboveRange, "–", "", null, ""), none);
    }

    private static AnalogValueState State(BoardState state)
    {
        return AnalogValueText.Show(Celsius, TenBitFiveVolts, state, 1).State;
    }

    private static BoardState Readings(params int?[] counts)
    {
        return Connected with { Analog = new AnalogReadings(counts) };
    }
}
