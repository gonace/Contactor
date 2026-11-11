namespace Contactor.Web.Persistence;

/// <summary>
/// The scheduler's progress: the last minute it handled. After a restart or a lost connection it catches up on
/// what it missed since then (at most 24 h back). One row.
/// </summary>
public sealed class SchedulerState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public DateTime? LastTickUtc { get; set; }
}
