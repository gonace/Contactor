namespace Contactor.Boards;

/// <summary>What kind of rule of its own a board has (<see cref="OwnRule"/>).</summary>
public enum OwnRuleKind
{
    /// <summary>A timer program or schedule on the board, e.g. the VM201's timer programs.</summary>
    Timer,

    /// <summary>The relay follows an input, and the board ignores the app's commands for it, e.g. the Waveshare (B)'s linkage mode.</summary>
    InputLinkage,

    /// <summary>A pulse on an input flips the relay, e.g. the Waveshare (B)'s toggle mode.</summary>
    InputToggle,

    /// <summary>Each change of an input flips the relay, e.g. the Waveshare (B)'s edge mode.</summary>
    InputEdge,
}
