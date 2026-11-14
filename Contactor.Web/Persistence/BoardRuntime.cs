namespace Contactor.Web.Persistence;

/// <summary>
/// What the app keeps about a board while it runs, one row per board: the scheduler's progress, and its contact with
/// the board over time, so an input's "since" survives a restart. The board only reports what it reads now, so a change
/// made while the app wasn't watching can't be timed; a short gap (a reboot, an upgrade) is taken as nothing having
/// changed.
/// </summary>
public sealed class BoardRuntime
{
    public int BoardId { get; set; }

    /// <summary>
    /// The last minute the scheduler handled. After a restart or a lost connection it catches up on what it missed since
    /// then (at most 24 h back).
    /// </summary>
    public DateTime? LastTickUtc { get; set; }

    /// <summary>The last time the app had contact with the board, saved every few minutes while it has (<c>BoardEventRecorder</c>).</summary>
    public DateTime? LastContactUtc { get; set; }

    /// <summary>
    /// When the app last came back after a long gap (no contact for longer than the recorder's limit). A change logged
    /// before it may have been followed by others the app didn't see.
    /// </summary>
    public DateTime? LongGapEndedUtc { get; set; }
}
