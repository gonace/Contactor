using System.Collections;
using System.Globalization;

namespace Contactor.Boards;

/// <summary>
/// Each relay's state after a power cut as the board last reported it (<see cref="BoardState.PowerOn"/>): one per relay,
/// relay 1 first, null for a relay whose state the app couldn't read or doesn't know. Immutable; two are equal when they
/// hold the same states in the same order.
/// </summary>
public sealed class PowerOnStates : IReadOnlyList<PowerOnState?>, IEquatable<PowerOnStates>
{
    private readonly PowerOnState?[] _states;

    /// <param name="states">One per relay, relay 1 first: 1 to <see cref="ChannelMask.MaxChannels"/> of them.</param>
    public PowerOnStates(IEnumerable<PowerOnState?> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _states = [.. states];
        if (_states.Length is 0 or > ChannelMask.MaxChannels)
        {
            throw new ArgumentException($"A board has 1 to {ChannelMask.MaxChannels} relays.", nameof(states));
        }

        if (Array.Find(_states, s => s is { } state && !Enum.IsDefined(state)) is { } undefined)
        {
            throw new ArgumentOutOfRangeException(nameof(states), undefined, "Not a state after a power cut.");
        }
    }

    /// <summary>How many relays: the board's.</summary>
    public int Count => _states.Length;

    /// <summary>The state of the relay at <paramref name="index"/>, 0 being relay 1.</summary>
    public PowerOnState? this[int index] => _states[index];

    /// <summary>The state of <paramref name="relay"/>, 1 being the first; null when unknown, or for a relay it doesn't have.</summary>
    public PowerOnState? For(int relay)
    {
        return relay >= 1 && relay <= _states.Length ? _states[relay - 1] : null;
    }

    public IEnumerator<PowerOnState?> GetEnumerator()
    {
        return ((IEnumerable<PowerOnState?>)_states).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Equals(PowerOnStates? other)
    {
        return other is not null && _states.AsSpan().SequenceEqual(other._states);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as PowerOnStates);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var state in _states)
        {
            hash.Add(state);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{string.Join(", ", _states.Select(s => s?.ToString() ?? "unknown"))}]");
    }

    public static bool operator ==(PowerOnStates? left, PowerOnStates? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(PowerOnStates? left, PowerOnStates? right)
    {
        return !(left == right);
    }
}
