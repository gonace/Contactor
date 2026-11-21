using Contactor.Boards;
using Contactor.Web.Features.Boards;
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

    // The top of the board's range may stand for any higher voltage: without a scale no value, never 5.00 V.
    [Fact]
    public void Should_ShowNoValue_AtTheTopOfTheBoardsRange_WithoutAScale()
    {
        Assert.Equal(new AnalogValue(AnalogValueState.AboveRange, "–", "", null, ""), Show(1023, AnalogScale.None));
        Assert.Equal(AnalogValueState.Value, Show(1022, AnalogScale.None).State);
    }

    // With a scale, the scale's value at the board's full scale, rounded as any value, after "≥", as it may be more;
    // after "≤" for a scale that falls, as it may be less.
    [Fact]
    public void Should_ShowTheScaledTopValue_WithASign_AtTheTopOfTheBoardsRange_WithAScale()
    {
        Assert.Equal(new AnalogValue(AnalogValueState.TopValue, "≥\u00A080.0", "°C", 5m, "5.00"), Show(1023, Celsius));
        Assert.True(AnalogValueText.Rises(Celsius));
        Assert.True(AnalogValueText.Rises(new AnalogScale(AnalogUnits.Percent, "", 5m, 100m, 0m, 0m)));

        // 0 V = 100 %, 5 V = 0 %.
        var falling = new AnalogScale(AnalogUnits.Percent, "", 0m, 100m, 5m, 0m);
        Assert.Equal("≤\u00A00.0", Show(1023, falling).Text);
        Assert.False(AnalogValueText.Rises(falling));

        using (TestCulture.Formats("sv-SE"))
        {
            Assert.Equal("≥\u00A080,0", Show(1023, Celsius).Text);
        }
    }

    // A point above what the board reads is warned of on boards that read less than it, and only on those, with what
    // the screens show at the top: a 0–5 V sensor (0–100 °C) on a 3.3 V board shows at most "≥ 66.0 °C".
    [Fact]
    public void Should_WarnOfAScaleBeyondTheBoardsRange_ByEachBoardsAbility()
    {
        var sensor = new AnalogScale(AnalogUnits.Celsius, "", 0m, 0m, 5m, 100m);
        var abilities = BoardCatalogue.All.Select(r => r.Model).Where(m => m.Capabilities.Analog is not null).ToList();
        Assert.NotEmpty(abilities);
        foreach (var model in abilities)
        {
            var ability = model.Capabilities.Analog!;
            var beyond = AnalogValueText.BeyondRange(sensor, ability);
            Assert.True((beyond is not null) == (ability.FullScaleVolts < 5m), model.Name);
        }

        var threeVolts = new AnalogAbility(1, 1023, 3.3m);
        Assert.Equal(new AnalogBeyondRange("3.3", "66.0\u00A0°C", "≥\u00A066.0\u00A0°C", true), AnalogValueText.BeyondRange(sensor, threeVolts));
        Assert.Null(AnalogValueText.BeyondRange(AnalogScale.None, threeVolts));
        Assert.Null(AnalogValueText.BeyondRange(sensor, null));

        // A point at the full scale itself is within the board's range.
        Assert.Null(AnalogValueText.BeyondRange(sensor with { Volts2 = 3.3m }, threeVolts));
    }

    // The note follows the points as typed, but only as they would save: rounded, and nothing for points out of
    // range, which could make the scaled value too large for a number while the page draws.
    [Fact]
    public void Should_NoteOnlyTypedPoints_AsTheyWouldSave()
    {
        var threeVolts = new AnalogAbility(1, 1023, 3.3m);
        var typed = AnalogInputsHandler.AsSaved(AnalogUnits.Celsius, "", 0m, 0m, 5.0004m, 100m);
        Assert.Equal(new AnalogScale(AnalogUnits.Celsius, "", 0m, 0m, 5m, 100m), typed);
        Assert.NotNull(AnalogValueText.BeyondRange(typed!, threeVolts));
        Assert.Equal("L", AnalogInputsHandler.AsSaved(AnalogUnits.Other, " L ", 0m, 0m, 5m, 100m)!.UnitText);

        Assert.Null(AnalogInputsHandler.AsSaved(AnalogUnits.Celsius, "", 0m, -decimal.MaxValue, 5m, decimal.MaxValue));
        Assert.Null(AnalogInputsHandler.AsSaved(AnalogUnits.Celsius, "", 0m, 0m, 101m, 100m));
        Assert.Null(AnalogInputsHandler.AsSaved(AnalogUnits.Celsius, "", 0m, 0m, null, 100m));
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
        return Show(counts, Celsius);
    }

    private static AnalogValue Show(int? counts, AnalogScale scale)
    {
        return AnalogValueText.Show(scale, TenBitFiveVolts, Connected with { Analog = new AnalogReadings([counts]) }, 1);
    }
}
