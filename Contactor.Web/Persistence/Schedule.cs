namespace Contactor.Web.Persistence;

/// <summary>
/// Turns a relay, or a group of relays, on and off on given days: exactly one of <see cref="RelayId"/> and
/// <see cref="GroupId"/> is set. Times are local (Europe/Stockholm); an <see cref="OffTime"/> before
/// <see cref="OnTime"/> runs over midnight and belongs to the day it started.
/// </summary>
public sealed class Schedule
{
    public int Id { get; set; }

    /// <summary>The relay it switches, or null for a group's schedule.</summary>
    public int? RelayId { get; set; }

    public Relay? Relay { get; set; }

    /// <summary>The group it starts and stops, or null for a relay's schedule.</summary>
    public int? GroupId { get; set; }

    public RelayGroup? Group { get; set; }

    public ScheduleDays Days { get; set; }

    public TimeOnly OnTime { get; set; }

    public TimeOnly OffTime { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary>The e-mail of the user who created it.</summary>
    public string CreatedBy { get; set; } = "";
}

/// <summary>Days of the week, as stored in <see cref="Schedule.Days"/>.</summary>
[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    All = Weekdays | Saturday | Sunday,
}
