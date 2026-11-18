using Contactor.Boards;

namespace Contactor.Web.Persistence;

/// <summary>
/// A rule of a board's own (<see cref="OwnRule"/>) the app has told people about, and what they decided: one row per
/// board, relay and kind of rule while the board reports it. Found when the board first reports it, deleted (and its
/// "Keep" with it) when an actual reading no longer has it, and deleted with the board, at a new address and with
/// another model. Never a copy of relay state: the board is the source of truth.
/// </summary>
public sealed class BoardOwnRule
{
    public int Id { get; set; }

    /// <summary>The board that has it.</summary>
    public int BoardId { get; set; }

    /// <summary>The relay it switches, by its channel on the board (1 being the first).</summary>
    public int Channel { get; set; }

    /// <summary>What kind of rule it is: one of <see cref="OwnRuleKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When the app first saw it.</summary>
    public DateTime FoundUtc { get; set; }

    /// <summary>When a person chose to keep it, so the app no longer warns about it; null: it warns.</summary>
    public DateTime? KeptUtc { get; set; }

    /// <summary>The e-mail of the person who kept it.</summary>
    public string? KeptBy { get; set; }
}

/// <summary>The kinds of rule as <see cref="BoardOwnRule.Kind"/> and the events save them, from <see cref="OwnRuleKind"/>.</summary>
public static class OwnRuleKinds
{
    public const string Timer = "timer";
    public const string InputLinkage = "input-linkage";
    public const string InputToggle = "input-toggle";
    public const string InputEdge = "input-edge";

    public const int MaxLength = 16;

    /// <summary>Every kind, in the order of <see cref="OwnRuleKind"/>.</summary>
    public static IReadOnlyList<string> All { get; } = [Timer, InputLinkage, InputToggle, InputEdge];

    /// <summary>The kind as it is saved.</summary>
    public static string For(OwnRuleKind kind)
    {
        return kind switch
        {
            OwnRuleKind.Timer => Timer,
            OwnRuleKind.InputLinkage => InputLinkage,
            OwnRuleKind.InputToggle => InputToggle,
            OwnRuleKind.InputEdge => InputEdge,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of rule."),
        };
    }

    /// <summary>The kind a saved text names; false for one this version doesn't know.</summary>
    public static bool TryParse(string? text, out OwnRuleKind kind)
    {
        switch (text)
        {
            case Timer:
                kind = OwnRuleKind.Timer;
                return true;
            case InputLinkage:
                kind = OwnRuleKind.InputLinkage;
                return true;
            case InputToggle:
                kind = OwnRuleKind.InputToggle;
                return true;
            case InputEdge:
                kind = OwnRuleKind.InputEdge;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
