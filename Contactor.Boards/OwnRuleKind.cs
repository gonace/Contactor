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

    /// <summary>
    /// The board turns the relay on again by itself some time after it goes off, e.g. the Shelly's auto-on: an "off" from
    /// the app or a person doesn't last.
    /// </summary>
    AutoOn,

    /// <summary>
    /// The board gives the relay a restart pulse by itself when a device it watches stops answering, e.g. the DAEnetIP2
    /// v2's ICMP monitoring with the relay's pin among its restart pins.
    /// </summary>
    Restart,
}
