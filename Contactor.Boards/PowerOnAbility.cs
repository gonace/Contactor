using System.Globalization;

namespace Contactor.Boards;

/// <summary>
/// What a board can do about its relays' state after a power cut (<see cref="PowerOnState"/>): the app reads it in the
/// board's state (<see cref="BoardState.PowerOn"/>) and sets it (<see cref="IRelayBoard.SetPowerOnAsync"/>), only when a
/// person saves it. A board that has it can do both. Immutable; two are equal when they have the same states and both
/// are per relay or both for the whole board.
/// </summary>
public sealed class PowerOnAbility : IEquatable<PowerOnAbility>
{
    /// <param name="states">The states the board has; each once, in <see cref="PowerOnState"/>'s order. At least one.</param>
    /// <param name="perRelay">
    /// Whether each relay has its own state, as a Shelly's switches do; false when one state is for the whole board,
    /// such as a "Save Outputs" setting, which setting it for any relay sets for all.
    /// </param>
    public PowerOnAbility(IEnumerable<PowerOnState> states, bool perRelay)
    {
        ArgumentNullException.ThrowIfNull(states);
        var all = states.ToArray();
        foreach (var state in all)
        {
            if (!Enum.IsDefined(state))
            {
                throw new ArgumentOutOfRangeException(nameof(states), state, "Not a state after a power cut.");
            }
        }

        States = [.. all.Distinct().Order()];
        if (States.Count == 0)
        {
            throw new ArgumentException("A board that has states after a power cut has at least one.", nameof(states));
        }

        PerRelay = perRelay;
    }

    /// <summary>The states the board has, each once, in <see cref="PowerOnState"/>'s order.</summary>
    public IReadOnlyList<PowerOnState> States { get; }

    /// <summary>Whether each relay has its own state; false when one state is for the whole board.</summary>
    public bool PerRelay { get; }

    /// <summary>Whether the board has <paramref name="state"/>; false for a value that isn't a state.</summary>
    public bool Has(PowerOnState state)
    {
        return States.Contains(state);
    }

    public bool Equals(PowerOnAbility? other)
    {
        return other is not null && PerRelay == other.PerRelay && States.SequenceEqual(other.States);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as PowerOnAbility);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PerRelay);
        foreach (var state in States)
        {
            hash.Add(state);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{string.Join(", ", States)}] {(PerRelay ? "per relay" : "for the whole board")}");
    }
}
