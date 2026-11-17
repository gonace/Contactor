namespace Contactor.Boards;

/// <summary>
/// A rule of the board's own that switches one of its relays without the app (<see cref="OwnRulesAbility"/>).
/// </summary>
/// <param name="Relay">The relay it switches, 1 being the first.</param>
/// <param name="Kind">What kind of rule it is.</param>
public sealed record OwnRule(int Relay, OwnRuleKind Kind)
{
    /// <summary>The relay it switches, 1 to <see cref="ChannelMask.MaxChannels"/>.</summary>
    public int Relay { get; } = Relay is >= 1 and <= ChannelMask.MaxChannels
        ? Relay
        : throw new ArgumentOutOfRangeException(nameof(Relay), Relay, $"A relay is numbered 1 to {ChannelMask.MaxChannels}.");

    /// <summary>What kind of rule it is.</summary>
    public OwnRuleKind Kind { get; } = Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Not a kind of rule.");
}
