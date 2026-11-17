namespace Contactor.Boards;

/// <summary>
/// What the app can know and do about a board's own rules (<see cref="OwnRule"/>): rules on the board itself that switch
/// its relays without the app, such as the VM201's timer programs. They compete with the app's schedules, and one that
/// turns a relay on can switch a heater on behind the app's back. The app never changes them without a person's press.
/// </summary>
/// <param name="Reads">
/// Whether the board reports its rules in its state (<see cref="BoardState.OwnRules"/>): which rules exist and which
/// relays they switch.
/// </param>
/// <param name="TurnsOff">Whether the app can turn them off (<see cref="IRelayBoard.TurnOffOwnRulesAsync"/>); only a board that reads them.</param>
/// <param name="MayHaveUnseen">
/// Whether the board may have rules the app can't see, so a person should check the board's own configuration.
/// </param>
public sealed record OwnRulesAbility(bool Reads, bool TurnsOff, bool MayHaveUnseen)
{
    /// <summary>The board has no rules of its own, and the app can tell for sure.</summary>
    public static OwnRulesAbility None { get; } = new(Reads: false, TurnsOff: false, MayHaveUnseen: false);

    /// <summary>The board may have rules of its own, and the app can't see them.</summary>
    public static OwnRulesAbility CantTell { get; } = new(Reads: false, TurnsOff: false, MayHaveUnseen: true);

    /// <summary>The board reports its rules in its state, and the app can turn them off.</summary>
    public static OwnRulesAbility ReadsAndTurnsOff { get; } = new(Reads: true, TurnsOff: true, MayHaveUnseen: false);

    /// <summary>Whether the app can turn them off: never a rule it can't read.</summary>
    public bool TurnsOff { get; } = !TurnsOff || Reads
        ? TurnsOff
        : throw new ArgumentException("A board can only turn off the rules it reads.", nameof(TurnsOff));
}
