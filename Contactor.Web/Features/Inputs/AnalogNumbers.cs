using System.Globalization;

namespace Contactor.Web.Features.Inputs;

/// <summary>The numbers typed on an analog input's page: its scale's voltages and values.</summary>
public static class AnalogNumbers
{
    /// <summary>The most characters a typed number is read from; a longer one is no number.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Reads a typed number, e.g. "−20", "2.08" or "2,08": either a point or a comma as its decimal mark, whatever the
    /// app's language, since a phone's keyboard may offer only one, and no thousands separator. A minus may be the
    /// hyphen or the minus sign a Swedish number is shown with. Empty is no number (null) and true; anything else that
    /// isn't a number is false.
    /// </summary>
    public static bool TryParse(string? text, out decimal? value)
    {
        value = null;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        var invariant = trimmed.Replace('−', '-').Replace(',', '.');
        if (invariant.Count(c => c == '.') > 1
            || !decimal.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        value = number;
        return true;
    }

    /// <summary>
    /// Which of two points (0 or 1) a new reading of <paramref name="volts"/> goes to: the first whose voltage is empty
    /// or not a number; with both set, the one nearer the reading, so the two points stay as far apart as they can.
    /// </summary>
    public static int Nearer(string? volts1, string? volts2, decimal volts)
    {
        if (!TryParse(volts1, out var first) || first is null)
        {
            return 0;
        }

        if (!TryParse(volts2, out var second) || second is null)
        {
            return 1;
        }

        return Math.Abs(second.Value - volts) < Math.Abs(first.Value - volts) ? 1 : 0;
    }
}
