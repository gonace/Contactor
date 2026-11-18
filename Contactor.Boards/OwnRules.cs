using System.Collections;

namespace Contactor.Boards;

/// <summary>
/// The rules of its own a board reported (<see cref="BoardState.OwnRules"/>), in the order the board gave them.
/// Immutable; two are equal when they hold the same rules in the same order.
/// </summary>
public sealed class OwnRules : IReadOnlyList<OwnRule>, IEquatable<OwnRules>
{
    private readonly OwnRule[] _rules;

    public OwnRules(IEnumerable<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        if (Array.Exists(_rules, r => r is null))
        {
            throw new ArgumentException("A rule can't be null.", nameof(rules));
        }

        Relays = ChannelMask.Of(_rules.Select(r => r.Relay));
        Unavailable = ChannelMask.Of(_rules.Where(r => r.Kind == OwnRuleKind.InputLinkage).Select(r => r.Relay));
    }

    /// <summary>A board that has none.</summary>
    public static OwnRules Empty { get; } = new([]);

    public int Count => _rules.Length;

    public OwnRule this[int index] => _rules[index];

    /// <summary>The relays any of the rules switches.</summary>
    public ChannelMask Relays { get; }

    /// <summary>
    /// The relays the board controls by itself and ignores the app's commands for (<see cref="OwnRuleKind.InputLinkage"/>):
    /// the app doesn't switch them until a person resets them.
    /// </summary>
    public ChannelMask Unavailable { get; }

    /// <summary>The rules that switch <paramref name="relay"/>, 1 being the first; none for a relay it doesn't have.</summary>
    public IReadOnlyList<OwnRule> For(int relay)
    {
        return [.. _rules.Where(r => r.Relay == relay)];
    }

    public IEnumerator<OwnRule> GetEnumerator()
    {
        return ((IEnumerable<OwnRule>)_rules).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Equals(OwnRules? other)
    {
        return other is not null && _rules.AsSpan().SequenceEqual(other._rules);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as OwnRules);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var rule in _rules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return $"[{string.Join(", ", _rules.Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Kind} {r.Relay}")))}]";
    }

    public static bool operator ==(OwnRules? left, OwnRules? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(OwnRules? left, OwnRules? right)
    {
        return !(left == right);
    }
}
