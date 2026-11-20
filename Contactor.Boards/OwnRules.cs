using System.Collections;

namespace Contactor.Boards;

/// <summary>
/// The rules of its own a board reported (<see cref="BoardState.OwnRules"/>), in the order the board gave them, and
/// what lies behind them on the board (<see cref="Units"/>), so the app can tell which rules a turn-off takes with it
/// (<see cref="GoesWith(IEnumerable{OwnRule})"/>), and which of them the app can't turn off (<see cref="CantTurnOff"/>).
/// Immutable; two are equal when they hold the same rules in the same order, the same units and the same rules the app
/// can't turn off.
/// </summary>
public sealed class OwnRules : IReadOnlyList<OwnRule>, IEquatable<OwnRules>
{
    private readonly OwnRule[] _rules;

    // Each unit's rules, each once, in the order of _rules; no empty unit and no unit twice.
    private readonly OwnRule[][] _units;

    // The rules the app can't turn off, each once, in the order of _rules.
    private readonly OwnRule[] _cantTurnOff;

    /// <summary>The board's rules, each on its own: a turn-off takes no other rule with it.</summary>
    public OwnRules(IEnumerable<OwnRule> rules)
        : this(rules, null)
    {
    }

    /// <summary>
    /// The board's rules, and the units behind them (<paramref name="units"/>): each unit is one thing on the board that
    /// makes the rules it holds, e.g. a schedule on the board that switches relays 1 and 2 makes relay 1's and relay 2's
    /// timers. Turning off a rule turns off every unit that holds it, and a rule goes too once every unit that holds it
    /// is off (<see cref="GoesWith(IEnumerable{OwnRule})"/>). A rule in no unit stands alone. Every rule a unit names must
    /// be one of <paramref name="rules"/>; an empty unit is left out, and a unit given twice counts once.
    /// <paramref name="cantTurnOff"/> are the rules the app can't turn off (<see cref="CantTurnOff"/>), each one of
    /// <paramref name="rules"/>; none by default.
    /// </summary>
    public OwnRules(IEnumerable<OwnRule> rules, IEnumerable<IReadOnlyCollection<OwnRule>>? units, IEnumerable<OwnRule>? cantTurnOff = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        if (Array.Exists(_rules, r => r is null))
        {
            throw new ArgumentException("A rule can't be null.", nameof(rules));
        }

        List<OwnRule[]> kept = [];
        foreach (var unit in units ?? [])
        {
            if (unit is null || unit.Any(r => r is null || !_rules.Contains(r)))
            {
                throw new ArgumentException("A unit holds only the board's own rules.", nameof(units));
            }

            OwnRule[] ordered = [.. _rules.Distinct().Where(unit.Contains)];
            if (ordered.Length > 0 && !kept.Exists(k => k.AsSpan().SequenceEqual(ordered)))
            {
                kept.Add(ordered);
            }
        }

        _units = [.. kept];
        HashSet<OwnRule> fixedRules = [];
        foreach (var rule in cantTurnOff ?? [])
        {
            if (rule is null || !_rules.Contains(rule))
            {
                throw new ArgumentException("Rules the app can't turn off must be among the board's rules.", nameof(cantTurnOff));
            }

            fixedRules.Add(rule);
        }

        _cantTurnOff = [.. _rules.Distinct().Where(fixedRules.Contains)];
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
    /// The units behind the rules, each its rules in the board's order (see the constructor); none for a board whose rules
    /// stand alone, the default.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<OwnRule>> Units => _units;

    /// <summary>
    /// The rules the app can't turn off, each once, in the board's order: a turn-off doesn't remove them
    /// (<see cref="IRelayBoard.TurnOffOwnRulesAsync"/> doesn't take them), e.g. a timer started on the VM208's own web
    /// page, which ends by itself. None by default. A screen offers no "Turn off" for them.
    /// </summary>
    public IReadOnlyList<OwnRule> CantTurnOff => _cantTurnOff;

    /// <summary>Whether a turn-off can remove <paramref name="rule"/>: false for one of <see cref="CantTurnOff"/>.</summary>
    public bool CanTurnOff(OwnRule rule)
    {
        return rule is not null && Array.IndexOf(_cantTurnOff, rule) < 0;
    }

    /// <summary>The other rules that turning off <paramref name="rule"/> also removes: <see cref="GoesWith(IEnumerable{OwnRule})"/> for it alone.</summary>
    public IReadOnlyList<OwnRule> GoesWith(OwnRule rule)
    {
        return rule is null ? [] : GoesWith([rule]);
    }

    /// <summary>
    /// The other rules that turning off <paramref name="rules"/> together also removes on the board
    /// (<see cref="IRelayBoard.TurnOffOwnRulesAsync"/>): every unit that holds one of them goes off, and a rule not asked
    /// goes too when every unit that holds it went off. Each once, in the board's order. None when the rules stand alone,
    /// the default, or for rules the board doesn't have; never a rule the app can't turn off (<see cref="CantTurnOff"/>),
    /// which stays.
    /// </summary>
    public IReadOnlyList<OwnRule> GoesWith(IEnumerable<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<OwnRule> asked = [.. rules.Where(r => r is not null)];
        var off = _units.Where(u => u.Any(asked.Contains)).ToList();
        if (off.Count == 0)
        {
            return [];
        }

        return [.. _rules.Distinct().Where(r => !asked.Contains(r)
            && CanTurnOff(r)
            && off.Exists(u => u.Contains(r))
            && _units.All(u => !u.Contains(r) || off.Contains(u)))];
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
            && _units.Length == other._units.Length
            && _units.All(u => Array.Exists(other._units, o => o.AsSpan().SequenceEqual(u)))
            && _cantTurnOff.AsSpan().SequenceEqual(other._cantTurnOff);
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

        hash.Add(_units.Length);
        hash.Add(_cantTurnOff.Length);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        static string Name(OwnRule r)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Kind} {r.Relay}");
        }

        var units = _units.Select(u => $"; ({string.Join(", ", u.Select(Name))})");
        var fixedRules = _cantTurnOff.Length == 0 ? "" : $"; can't turn off: {string.Join(", ", _cantTurnOff.Select(Name))}";
        return $"[{string.Join(", ", _rules.Select(Name))}{string.Concat(units)}{fixedRules}]";
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
