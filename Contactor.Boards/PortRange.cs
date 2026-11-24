using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Contactor.Boards;

/// <summary>
/// The TCP ports from <paramref name="First"/> to <paramref name="Last"/>, both included, written <c>first-last</c>,
/// e.g. <c>10000-10020</c>. The public demo gives each visitor's copy one of its own (Issue #606).
/// </summary>
public sealed record PortRange(int First, int Last)
{
    /// <summary>How many ports it has.</summary>
    public int Count => Last - First + 1;

    /// <summary>
    /// Reads <c>first-last</c>: two port numbers of digits only, from 1 to 65535, the first no larger than the last. Null
    /// for anything else, so a range that can't be read never widens what it allows.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PortRange? range)
    {
        range = null;
        var parts = (text ?? "").Split('-');
        if (parts.Length != 2 || !TryParsePort(parts[0], out var first) || !TryParsePort(parts[1], out var last) || first > last)
        {
            return false;
        }

        range = new PortRange(first, last);
        return true;
    }

    public bool Contains(int port)
    {
        return port >= First && port <= Last;
    }

    /// <summary>Whether the two ranges have a port in common.</summary>
    public bool Overlaps(PortRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return First <= other.Last && other.First <= Last;
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{First}-{Last}");
    }

    // Digits only: no sign, no spaces, no thousands separators. At most 5 of them, so it never overflows.
    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (text.Length is 0 or > 5 || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        port = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535;
    }
}
