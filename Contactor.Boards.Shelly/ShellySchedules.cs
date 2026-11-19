namespace Contactor.Boards.Shelly;

/// <summary>One schedule job on the device, as far as the app reads it ("Schedule", "Schedule Object Structure").</summary>
/// <param name="Id">The job's <c>id</c>, which <c>Schedule.Update</c> names.</param>
/// <param name="Enabled">Its <c>enable</c>: true by default, as the docs say.</param>
/// <param name="Relays">
/// The relays its calls switch, bit 0 = relay 1: <c>Switch.Set</c> and <c>Switch.Toggle</c> with an <c>id</c>,
/// <c>Switch.SetAll</c> (every relay) and <c>Switch.SetMany</c> with <c>ids</c>. Calls the app can't read, ids the device
/// hasn't and other methods leave no bit.
/// </param>
public sealed record ShellyJob(long Id, bool Enabled, byte Relays);

/// <summary>What <c>Schedule.List</c> says: every job, in the device's order, and the revision.</summary>
/// <param name="Jobs">The jobs, at most <see cref="ShellyApi.MaxScheduleJobs"/>.</param>
/// <param name="Revision">Its <c>rev</c>; null when it has none, or one that isn't a whole number.</param>
public sealed record ShellySchedules(IReadOnlyList<ShellyJob> Jobs, long? Revision)
{
    /// <summary>The relays an enabled job switches, bit 0 = relay 1.</summary>
    public byte Relays => (byte)Jobs.Where(j => j.Enabled).Aggregate(0, (mask, job) => mask | job.Relays);

    /// <summary>The enabled jobs that switch <paramref name="relay"/>, 1 being the first, in the device's order.</summary>
    public IReadOnlyList<ShellyJob> Switching(int relay)
    {
        return relay is >= 1 and <= 8 ? [.. Jobs.Where(j => j.Enabled && (j.Relays & (1 << (relay - 1))) != 0)] : [];
    }

    public bool Equals(ShellySchedules? other)
    {
        return other is not null && Revision == other.Revision && Jobs.SequenceEqual(other.Jobs);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Revision, Jobs.Count);
    }
}
