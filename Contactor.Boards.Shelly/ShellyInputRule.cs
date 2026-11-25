namespace Contactor.Boards.Shelly;

/// <summary>
/// The own rule a switch's input makes on the device (<see cref="ShellyApi.ToInputRule"/>): <see cref="OwnRuleKind.InputEdge"/>,
/// <see cref="OwnRuleKind.InputToggle"/>, or none (<see cref="None"/>) for an input that switches nothing. With
/// <paramref name="Cycle"/> (<c>in_mode</c> <c>cycle</c>), a press steps both relays of a 2-relay device, not only this
/// switch's: an <see cref="OwnRuleKind.InputToggle"/> on relays 1 and 2.
/// </summary>
public readonly record struct ShellyInputRule(OwnRuleKind? Kind, bool Cycle = false)
{
    /// <summary>An input that switches nothing: <c>detached</c>, or locked.</summary>
    public static ShellyInputRule None { get; }
}
