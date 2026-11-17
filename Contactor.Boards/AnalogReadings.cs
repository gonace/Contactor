using System.Collections;

namespace Contactor.Boards;

/// <summary>
/// What a board's analog inputs read, in counts of its converter (<see cref="AnalogAbility"/>): one per input, input 1
/// first. Null for an input without a reading, e.g. one the board's answer left out or gave outside 0 to
/// <see cref="AnalogAbility.MaxCounts"/>. Immutable; two are equal when they hold the same readings.
/// </summary>
public sealed class AnalogReadings : IReadOnlyList<int?>, IEquatable<AnalogReadings>
{
    private readonly int?[] _counts;

    public AnalogReadings(IEnumerable<int?> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        _counts = [.. counts];
    }

    public int Count => _counts.Length;

    /// <summary>The reading of an input, 0 being input 1.</summary>
    public int? this[int index] => _counts[index];

    /// <summary>The reading of an input, 1 being the first; null for an input without a reading or one it doesn't have.</summary>
    public int? Input(int input)
    {
        return input >= 1 && input <= _counts.Length ? _counts[input - 1] : null;
    }

    public IEnumerator<int?> GetEnumerator()
    {
        return ((IEnumerable<int?>)_counts).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Equals(AnalogReadings? other)
    {
        return other is not null && _counts.AsSpan().SequenceEqual(other._counts);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as AnalogReadings);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var count in _counts)
        {
            hash.Add(count);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return $"[{string.Join(", ", _counts.Select(c => c?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"))}]";
    }

    public static bool operator ==(AnalogReadings? left, AnalogReadings? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(AnalogReadings? left, AnalogReadings? right)
    {
        return !(left == right);
    }
}
