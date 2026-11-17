namespace Contactor.Web.Persistence;

/// <summary>
/// One of the board's analog inputs, e.g. a temperature or pressure sensor. Seeded at start, one per analog input on
/// the board, and deleted only with its board. Not an <see cref="Input"/>: an input is a contact, closed or open. Its
/// reading is not stored: the board is the source of truth.
/// </summary>
public sealed class AnalogInput
{
    public const int NameMaxLength = 40;

    public const int UnitTextMaxLength = 8;

    /// <summary>The database's key for the analog input, in links; given by the database, and not its channel.</summary>
    public int Id { get; set; }

    /// <summary>The board it is on.</summary>
    public int BoardId { get; set; }

    /// <summary>
    /// Its analog input on that board, 1 to the board's analog input count; unique per board. The board's readings go by
    /// it, and the screens show it ("ANALOG 1").
    /// </summary>
    public int Channel { get; set; }

    public string Name { get; set; } = "";

    /// <summary>False hides it from the list by default, e.g. for an input wired to nothing.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The unit its value is shown in: one of <see cref="AnalogUnits"/>; "" (none chosen) is volts.</summary>
    public string Unit { get; set; } = AnalogUnits.None;

    /// <summary>The unit's own text with <see cref="AnalogUnits.Other"/>, e.g. "kPa"; "" with any other unit.</summary>
    public string UnitText { get; set; } = "";

    /// <summary>
    /// The scale's first point: the board reads <see cref="Volts1"/> V when the value is <see cref="Value1"/>. The four
    /// are all set or all null; null is no scale, and the value shown is the voltage.
    /// </summary>
    public decimal? Volts1 { get; set; }

    public decimal? Value1 { get; set; }

    /// <summary>The scale's second point, at another voltage than the first.</summary>
    public decimal? Volts2 { get; set; }

    public decimal? Value2 { get; set; }

    /// <summary>Whether it has a scale: all four points are set.</summary>
    public bool HasScale => Volts1 is not null && Value1 is not null && Volts2 is not null && Value2 is not null;

    /// <summary>
    /// The name an analog input is seeded with, until someone gives it one: "Analog 1", after its channel. Saved as a
    /// name, so it stays in the language it was made in.
    /// </summary>
    public static string DefaultName(int channel)
    {
        return $"Analog {channel}";
    }
}

/// <summary>The units an analog input's value can be shown in, as <see cref="AnalogInput.Unit"/> saves them.</summary>
public static class AnalogUnits
{
    /// <summary>None chosen: the value is volts.</summary>
    public const string None = "";

    public const string Celsius = "C";

    public const string Percent = "%";

    public const string Bar = "bar";

    public const string Volts = "V";

    /// <summary>A unit of the person's own, in <see cref="AnalogInput.UnitText"/>.</summary>
    public const string Other = "other";

    public const int MaxLength = 8;

    /// <summary>Every unit, in the order the screens offer them.</summary>
    public static IReadOnlyList<string> All { get; } = [None, Celsius, Percent, Bar, Volts, Other];

    /// <summary>Whether the value is in volts: no unit chosen, or "V".</summary>
    public static bool IsVolts(string unit)
    {
        return unit is None or Volts;
    }
}
