namespace Contactor.Web.Persistence;

/// <summary>
/// The app's contact with the board over time, so an input's "since" survives a restart. One row, kept by
/// <c>BoardEventRecorder</c>. The board only reports what it reads now, so a change made while the app wasn't
/// watching can't be timed; a short gap (a reboot, an upgrade) is taken as nothing having changed.
/// </summary>
public sealed class BoardWatchState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>The last time the app had contact with the board, saved about once a minute while it has.</summary>
    public DateTime? LastContactUtc { get; set; }

    /// <summary>
    /// When the app last came back after a long gap (no contact for longer than the recorder's limit). A change logged
    /// before it may have been followed by others the app didn't see.
    /// </summary>
    public DateTime? LongGapEndedUtc { get; set; }
}
