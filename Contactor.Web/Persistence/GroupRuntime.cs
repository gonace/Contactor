namespace Contactor.Web.Persistence;

/// <summary>
/// The app's record of a group's sequence in progress, so a start or a run-on carries on after a restart. Which
/// relays are on is never stored: the board's status is the truth, and the sequence follows it.
/// </summary>
public sealed class GroupRuntime
{
    public int GroupId { get; set; }

    /// <summary>One of <see cref="GroupPhases"/>.</summary>
    public string Phase { get; set; } = GroupPhases.Idle;

    /// <summary>How many steps, from the top, the sequence is heading for: all of them when starting, 0 when stopping.</summary>
    public int TargetLevel { get; set; }

    /// <summary>When the next step may go: the start delay or the run-on time after the last confirmed step.</summary>
    public DateTime? NextStepAtUtc { get; set; }

    /// <summary>The position of the step a command was sent for and the board hasn't confirmed yet.</summary>
    public int? PendingStep { get; set; }

    /// <summary>The one relay of <see cref="PendingStep"/> the command was for; null when it was for the whole step.</summary>
    public int? PendingRelayId { get; set; }

    /// <summary>Whether that command was ON.</summary>
    public bool PendingOn { get; set; }

    public DateTime? PendingSinceUtc { get; set; }

    /// <summary>
    /// While stopping to <see cref="TargetLevel"/> after one relay was turned off: that relay, the only one of the step
    /// below the target that goes off. The other relays of its step stay as they are. Null stops whole steps.
    /// </summary>
    public int? StopRelayId { get; set; }

    public DateTime? OnSinceUtc { get; set; }

    public DateTime? AutoOffAtUtc { get; set; }

    /// <summary>An e-mail, or one of <see cref="EventActors"/>.</summary>
    public string? TurnedOnBy { get; set; }
}

/// <summary>What a group's sequence is doing.</summary>
public static class GroupPhases
{
    public const string Idle = "idle";
    public const string Starting = "starting";
    public const string Stopping = "stopping";

    public const int MaxLength = 16;
}
