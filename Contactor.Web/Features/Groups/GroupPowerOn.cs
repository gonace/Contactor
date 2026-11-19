using Contactor.Boards;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// The one rule for a group's relays after a power cut (Issue #516): every relay in a group is
/// <see cref="PowerOnState.Off"/> on the board. A board that turns any of them on by itself when the power comes back,
/// "On", "As before" or "Follows its switch", does it before Contactor runs and without the group's start order, e.g. the
/// heat without the fan, or both at once. So Contactor starts a group in order once it runs, and the board never does.
/// Every screen and handler asks here; nothing else decides it. Pure: it reads only what it is given.
/// </summary>
public static class GroupPowerOn
{
    /// <summary>The only state a relay in a group may have after a power cut.</summary>
    public const PowerOnState Safe = PowerOnState.Off;

    /// <summary>Whether a relay in a group may have <paramref name="state"/> after a power cut: only <see cref="Safe"/>.</summary>
    public static bool IsAllowed(PowerOnState state)
    {
        return state == Safe;
    }

    /// <summary>
    /// The relays among <paramref name="channels"/>, in the order given and each once, whose board reports another state
    /// than <see cref="Safe"/> (<see cref="BoardState.PowerOn"/>). None for a board without the ability
    /// (<paramref name="ability"/> null: it can't tell), before it reported its states, and for a relay whose state is
    /// unknown: only a state the board reported breaks the rule.
    /// </summary>
    public static IReadOnlyList<int> NotSafe(PowerOnAbility? ability, PowerOnStates? states, IEnumerable<int> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (ability is null || states is null)
        {
            return [];
        }

        return [.. channels.Distinct().Where(c => states.For(c) is { } state && !IsAllowed(state))];
    }

    /// <summary>
    /// The relays among <paramref name="channels"/>, in the order given and each once, whose state after a power cut a
    /// board with the ability hasn't reported: not read yet, or a read that failed. None for a board without the ability:
    /// it can't tell at all.
    /// </summary>
    public static IReadOnlyList<int> Unknown(PowerOnAbility? ability, PowerOnStates? states, IEnumerable<int> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (ability is null)
        {
            return [];
        }

        return [.. channels.Distinct().Where(c => states?.For(c) is null)];
    }

    /// <summary>
    /// The relays "Create group" and a group's Save set to <see cref="Safe"/> for its new relays, on the person's press:
    /// those that break the rule (<see cref="NotSafe"/>) and those whose state is unknown (<see cref="Unknown"/>), in
    /// the order given and each once. "Make it safe" only acts on <see cref="NotSafe"/>: an unknown state gives no
    /// warning.
    /// </summary>
    public static IReadOnlyList<int> ToSetOnSave(PowerOnAbility? ability, PowerOnStates? states, IEnumerable<int> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        List<int> all = [.. channels.Distinct()];
        var notSafe = NotSafe(ability, states, all);
        var unknown = Unknown(ability, states, all);
        return [.. all.Where(c => notSafe.Contains(c) || unknown.Contains(c))];
    }

    /// <summary>
    /// The relays to write <see cref="Safe"/> to, so that each of <paramref name="notSafe"/> is Off: each of
    /// them on a board with a state per relay, and only the first on a board with one state for every relay
    /// (<see cref="PowerOnAbility.PerRelay"/> false), where one write sets them all.
    /// </summary>
    public static IReadOnlyList<int> Writes(PowerOnAbility ability, IReadOnlyList<int> notSafe)
    {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentNullException.ThrowIfNull(notSafe);
        return ability.PerRelay ? [.. notSafe.Distinct()] : [.. notSafe.Take(1)];
    }
}
