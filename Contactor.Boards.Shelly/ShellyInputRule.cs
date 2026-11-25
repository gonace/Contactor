namespace Contactor.Boards.Shelly;

/// <summary>
/// The own rule a switch's input makes on the device (<see cref="ShellyApi.ToInputRule"/>): <see cref="OwnRuleKind.InputEdge"/>,
/// <see cref="OwnRuleKind.InputToggle"/>, or none (<see cref="None"/>) for an input that switches nothing.
/// </summary>
public readonly record struct ShellyInputRule(OwnRuleKind? Kind)
{
    /// <summary>An input that switches nothing: <c>detached</c>, or locked.</summary>
    public static ShellyInputRule None { get; }
}
