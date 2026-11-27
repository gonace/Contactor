namespace Contactor.Boards;

/// <summary>
/// What a turn-off of the board's own rules did (<see cref="IRelayBoard.TurnOffOwnRulesAsync"/>), as the board confirmed
/// it. A rule can sit in several units (<see cref="OwnRules.Units"/>, e.g. a smartDEN's rows or a Shelly's schedules), so
/// a unit can go off while another unit of the same rule is refused: that rule isn't taken, but it changed, and so did
/// the other rules in the unit that went off (issue #604). Each rule is in one list at most, each list in relay order.
/// </summary>
public sealed class OwnRulesTurnOff
{
    /// <summary>A turn-off that took nothing and sent nothing: none asked.</summary>
    public static OwnRulesTurnOff None { get; } = new([], [], []);

    /// <param name="taken">The rules asked that the board took: every unit behind them went off.</param>
    /// <param name="alsoOff">The other rules now off because every unit behind them went off, e.g. relay 2's timer in a schedule that also switches relay 1.</param>
    /// <param name="partlyOff">The rules, asked or not, of which some units went off and others didn't: they still switch their relays.</param>
    public OwnRulesTurnOff(IReadOnlyList<OwnRule> taken, IReadOnlyList<OwnRule> alsoOff, IReadOnlyList<OwnRule> partlyOff)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentNullException.ThrowIfNull(alsoOff);
        ArgumentNullException.ThrowIfNull(partlyOff);
        Taken = taken;
        AlsoOff = alsoOff;
        PartlyOff = partlyOff;
    }

    /// <summary>The rules asked that the board took: every unit behind them went off.</summary>
    public IReadOnlyList<OwnRule> Taken { get; }

    /// <summary>The other rules now off because every unit behind them went off.</summary>
    public IReadOnlyList<OwnRule> AlsoOff { get; }

    /// <summary>The rules, asked or not, of which some units went off and others didn't: they still switch their relays.</summary>
    public IReadOnlyList<OwnRule> PartlyOff { get; }

    /// <summary>Whether anything went off on the board.</summary>
    public bool AnyOff => Taken.Count > 0 || AlsoOff.Count > 0 || PartlyOff.Count > 0;

    /// <summary>The rules <paramref name="taken"/> on a board whose rules stand alone: nothing else goes with them.</summary>
    public static OwnRulesTurnOff Of(IReadOnlyList<OwnRule> taken)
    {
        return new OwnRulesTurnOff(taken, [], []);
    }

    /// <summary>
    /// The rules <paramref name="taken"/>, and what else went off with the <paramref name="units"/> the board turned off:
    /// each unit's rules and whether the board confirmed it off. A rule not taken in a unit that went off is also off
    /// when every unit that holds it went off, otherwise partly off. Never a rule of <paramref name="cantTurnOff"/>
    /// (<see cref="OwnRules.CantTurnOff"/>), which stays, as <see cref="OwnRules.GoesWith(IEnumerable{OwnRule})"/> says.
    /// </summary>
    public static OwnRulesTurnOff From(
        IReadOnlyList<OwnRule> taken,
        IEnumerable<(IReadOnlyCollection<OwnRule> Rules, bool Off)> units,
        IEnumerable<OwnRule>? cantTurnOff = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentNullException.ThrowIfNull(units);
        List<(IReadOnlyCollection<OwnRule> Rules, bool Off)> all = [.. units];
        HashSet<OwnRule> done = [.. taken];
        HashSet<OwnRule> stays = [.. cantTurnOff ?? []];
        List<OwnRule> touched = [.. all.Where(u => u.Off).SelectMany(u => u.Rules).Distinct()
            .Where(r => !done.Contains(r) && !stays.Contains(r))
            .OrderBy(r => r.Relay).ThenBy(r => r.Kind)];
        List<OwnRule> also = [.. touched.Where(r => all.Where(u => u.Rules.Contains(r)).All(u => u.Off))];
        return new OwnRulesTurnOff(taken, also, [.. touched.Except(also)]);
    }
}
