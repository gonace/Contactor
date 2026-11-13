namespace Contactor.Web.Persistence;

/// <summary>
/// Internal state for auto-off: when a relay was turned on manually, by whom, and when it must go off.
/// </summary>
public sealed class RelayRuntime
{
    public int RelayId { get; set; }

    public DateTime? OnSinceUtc { get; set; }

    public DateTime? AutoOffAtUtc { get; set; }

    public string? TurnedOnBy { get; set; }

    /// <summary>
    /// Goes up by one at every save of the row (<see cref="RelayRunVersionInterceptor"/>), and a save checks it: one
    /// made from a row that someone else changed since it was read fails with a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of overwriting their change.
    /// </summary>
    public long Version { get; set; }
}
