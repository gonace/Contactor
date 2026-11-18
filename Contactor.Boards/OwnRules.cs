using System.Collections;

namespace Contactor.Boards;

/// <summary>
/// The rules of its own a board reported (<see cref="BoardState.OwnRules"/>), in the order the board gave them, and
/// which of them a turn-off takes with it (<see cref="GoesWith(OwnRule)"/>). Immutable; two are equal when they hold the
/// same rules in the same order, with the same rules going together.
/// </summary>
public sealed class OwnRules : IReadOnlyList<OwnRule>, IEquatable<OwnRules>
{
    private readonly OwnRule[] _rules;

    // For a rule, the other rules turning it off also removes, in the order of _rules; only rules that take some.
    private readonly Dictionary<OwnRule, OwnRule[]> _goesWith = [];

    /// <summary>The board's rules, and none going together.</summary>
    public OwnRules(IEnumerable<OwnRule> rules)
        : this(rules, null)
    {
    }

    /// <summary>
    /// The board's rules, and for a rule the other rules that turning it off also removes on the board
    /// (<paramref name="goesWith"/>, see <see cref="GoesWith(OwnRule)"/>), e.g. a schedule on the board that switches
    /// relays 1 and 2: turning off relay 1's timer turns off the schedule, so relay 2's timer goes too unless another
    /// schedule switches relay 2. Not symmetric: each rule says what its own turn-off takes. A rule that takes none
    /// needn't be named, and a rule never goes with itself. Every rule named must be one of <paramref name="rules"/>.
    /// </summary>
    public OwnRules(IEnumerable<OwnRule> rules, IReadOnlyDictionary<OwnRule, IReadOnlyCollection<OwnRule>>? goesWith)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        if (Array.Exists(_rules, r => r is null))
        {
            throw new ArgumentException("A rule can't be null.", nameof(rules));
        }

        foreach (var (rule, others) in goesWith ?? Enumerable.Empty<KeyValuePair<OwnRule, IReadOnlyCollection<OwnRule>>>())
        {
            if (others is null || others.Any(o => o is null || !_rules.Contains(o)) || !_rules.Contains(rule))
            {
                throw new ArgumentException("Only the board's own rules can go together.", nameof(goesWith));
            }

            OwnRule[] taken = [.. _rules.Distinct().Where(r => r != rule && others.Contains(r))];
            if (taken.Length > 0)
            {
                _goesWith[rule] = taken;
            }
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

    /// <summary>
    /// The other rules that turning off <paramref name="rule"/> also removes on the board
    /// (<see cref="IRelayBoard.TurnOffOwnRulesAsync"/>), each once, in the board's order: all of them, not only those the
    /// board links directly. None for a rule that goes alone, the default, or one the board doesn't have.
    /// </summary>
    public IReadOnlyList<OwnRule> GoesWith(OwnRule rule)
    {
        return rule is not null && _goesWith.TryGetValue(rule, out var others) ? others : [];
    }

    /// <summary>
    /// The other rules that turning off <paramref name="rules"/> together also removes: those that go with any of them
    /// (<see cref="GoesWith(OwnRule)"/>) and aren't asked, each once, in the board's order. Turning off several at once
    /// may take more than these on some boards, which the next state shows.
    /// </summary>
    public IReadOnlyList<OwnRule> GoesWith(IEnumerable<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<OwnRule> asked = [.. rules.Where(r => r is not null)];
        HashSet<OwnRule> taken = [.. asked.SelectMany(GoesWith)];
        return [.. _rules.Distinct().Where(r => taken.Contains(r) && !asked.Contains(r))];
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
        return other is not null
            && _rules.AsSpan().SequenceEqual(other._rules)
            && _goesWith.Count == other._goesWith.Count
            && _goesWith.All(g => other._goesWith.TryGetValue(g.Key, out var others) && g.Value.AsSpan().SequenceEqual(others));
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

        hash.Add(_goesWith.Count);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        static string Name(OwnRule r)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Kind} {r.Relay}");
        }

        var links = _rules.Distinct().Where(_goesWith.ContainsKey).Select(r => $"; {Name(r)} takes {string.Join(", ", _goesWith[r].Select(Name))}");
        return $"[{string.Join(", ", _rules.Select(Name))}{string.Concat(links)}]";
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
