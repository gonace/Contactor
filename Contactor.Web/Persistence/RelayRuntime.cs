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
}
