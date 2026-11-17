using System.Globalization;
using Contactor.Boards;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Inputs;

/// <summary>
/// How an analog input's voltage becomes the value shown: its unit, and its scale's two points, where the board reads
/// <see cref="Volts1"/> V at <see cref="Value1"/> and <see cref="Volts2"/> V at <see cref="Value2"/>. Without a scale
/// the value is the voltage.
/// </summary>
public sealed record AnalogScale(string Unit, string UnitText, decimal? Volts1, decimal? Value1, decimal? Volts2, decimal? Value2)
{
    /// <summary>No unit chosen and no scale: the value is the voltage.</summary>
    public static AnalogScale None { get; } = new(AnalogUnits.None, "", null, null, null, null);

    /// <summary>
    /// Whether it has a scale: both points, at two voltages and with two values. Two points that share either are no
    /// scale (the handler never saves them), so a scale's slope is never 0 and never a division by 0.
    /// </summary>
    public bool HasScale =>
        Volts1 is { } volts1 && Value1 is { } value1 && Volts2 is { } volts2 && Value2 is { } value2 && volts1 != volts2 && value1 != value2;

    /// <summary>An analog input's unit and scale, as saved.</summary>
    public static AnalogScale Of(AnalogInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new AnalogScale(input.Unit, input.UnitText, input.Volts1, input.Value1, input.Volts2, input.Value2);
    }
}

/// <summary>What an analog input's card shows, by what the app knows of its board.</summary>
public enum AnalogValueState
{
    /// <summary>The board hasn't answered yet, or hasn't read its analog inputs yet: shown grey.</summary>
    Waiting,

    /// <summary>The app has no contact with the board: "–", muted, as a reading from before could be stale.</summary>
    NoContact,

    /// <summary>The board answers, but gave no reading for this input: "–", and "check the sensor".</summary>
    NoReading,

    /// <summary>The reading is the top of the board's range, so the voltage may be higher: "–".</summary>
    AboveRange,

    /// <summary>A value to show.</summary>
    Value,
}

/// <summary>
/// An analog input's value as the screens show it. <paramref name="Text"/> is the number, or "–" without one;
/// <paramref name="Unit"/> its unit ("°C"), "" without a value. <paramref name="Volts"/> and <paramref name="VoltsText"/>
/// are what the board reads at the terminal, null and "" without a value.
/// </summary>
public sealed record AnalogValue(AnalogValueState State, string Text, string Unit, decimal? Volts, string VoltsText)
{
    /// <summary>What the screens show for an input without a value.</summary>
    public const string NoValue = "–";
}

/// <summary>
/// Turns an analog input's reading into the text shown, in the current culture: scaled by its two points, and with as
/// many decimals as the board can measure, so a value never shows digits the board can't tell apart. Pure: no clock, no
/// database, no texts.
/// </summary>
public static class AnalogValueText
{
    /// <summary>The most decimals a value shows.</summary>
    public const int MaxDecimals = 3;

    /// <summary>
    /// How many decimals a value shows: one count of the board's converter is a step of
    /// <paramref name="voltsPerCount"/> × the scale's slope in the value; the decimals are the most that still tell two
    /// steps apart, floor(−log10(step)), from 0 to <see cref="MaxDecimals"/>. A 10-bit board's 0–5 V shown as volts
    /// gives 2 (a step of 0.0049 V); scaled to −20…80 °C it gives 1 (0.098 °C).
    /// </summary>
    public static int Decimals(decimal voltsPerCount, AnalogScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        var step = (double)voltsPerCount * Math.Abs(Slope(scale));
        if (!(step > 0))
        {
            // Only for a board's volts per count of 0 or less, which AnalogAbility never gives: a scale's slope is
            // never 0 (AnalogScale.HasScale).
            return MaxDecimals;
        }

        // A little over, so a step of exactly 0.1 gives 1 and not 0 from a log10 a hair under 1.
        var decimals = Math.Floor(-Math.Log10(step) + 1e-9);
        return (int)Math.Clamp(decimals, 0, MaxDecimals);
    }

    /// <summary>The value at <paramref name="volts"/>: on the line through the scale's two points; the voltage without a scale.</summary>
    public static decimal Scaled(decimal volts, AnalogScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        if (!scale.HasScale)
        {
            return volts;
        }

        // Multiplied first, so a point's own voltage gives its value exactly.
        return scale.Value1!.Value + ((volts - scale.Volts1!.Value) * (scale.Value2!.Value - scale.Value1.Value) / (scale.Volts2!.Value - scale.Volts1.Value));
    }

    /// <summary>
    /// The unit shown after the value: "°C", "%", "bar", the person's own text with "Other", and "V" for volts, which
    /// is also what a value without a unit chosen is.
    /// </summary>
    public static string Unit(AnalogScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        return scale.Unit switch
        {
            AnalogUnits.Celsius => "°C",
            AnalogUnits.Percent => "%",
            AnalogUnits.Bar => "bar",
            AnalogUnits.Other => scale.UnitText,
            _ => "V",
        };
    }

    /// <summary>A number with <paramref name="decimals"/> decimals, in the current culture: "21.4", "21,4" in Swedish.</summary>
    public static string Number(decimal value, int decimals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, MaxDecimals);
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);

        // A value that rounds to zero from below shows as 0, never "-0.0".
        return (rounded == 0m ? 0m : rounded).ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// What the analog input on <paramref name="channel"/> shows now, from its board's latest state.
    /// </summary>
    /// <param name="scale">The input's unit and scale.</param>
    /// <param name="ability">The board's analog inputs; null for a board that has none, which shows as waiting.</param>
    /// <param name="state">The board's latest state.</param>
    /// <param name="channel">The input on the board, 1 being the first.</param>
    public static AnalogValue Show(AnalogScale scale, AnalogAbility? ability, BoardState state, int channel)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(state);
        if (ability is null || !state.HasAnswered)
        {
            return Empty(AnalogValueState.Waiting);
        }

        if (!state.IsConnected)
        {
            return Empty(AnalogValueState.NoContact);
        }

        if (state.Analog is not { } readings)
        {
            return Empty(AnalogValueState.Waiting);
        }

        if (ability.Check(readings.Input(channel)) is not { } counts)
        {
            return Empty(AnalogValueState.NoReading);
        }

        if (ability.IsFullScale(counts))
        {
            return Empty(AnalogValueState.AboveRange);
        }

        var volts = ability.Volts(counts);
        var text = Number(Scaled(volts, scale), Decimals(ability.VoltsPerCount, scale));
        var voltsText = Number(volts, Decimals(ability.VoltsPerCount, AnalogScale.None));
        return new AnalogValue(AnalogValueState.Value, text, Unit(scale), volts, voltsText);
    }

    private static AnalogValue Empty(AnalogValueState state)
    {
        return new AnalogValue(state, AnalogValue.NoValue, "", null, "");
    }

    // How much the value changes per volt: 1 without a scale.
    private static double Slope(AnalogScale scale)
    {
        return scale.HasScale
            ? (double)(scale.Value2!.Value - scale.Value1!.Value) / (double)(scale.Volts2!.Value - scale.Volts1!.Value)
            : 1d;
    }
}
