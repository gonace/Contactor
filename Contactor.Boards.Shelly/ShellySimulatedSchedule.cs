namespace Contactor.Boards.Shelly;

/// <summary>A schedule job the simulated Shelly holds, as <c>Schedule.List</c> gives it: a snapshot.</summary>
/// <param name="Calls">Its calls as the JSON array they were given in.</param>
public sealed record ShellySimulatedSchedule(long Id, bool Enable, string Timespec, string Calls);
