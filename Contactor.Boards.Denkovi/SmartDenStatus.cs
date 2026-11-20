namespace Contactor.Boards.Denkovi;

/// <summary>One answer to a poll: which relays are on, with the week schedule as last read.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Schedule">
/// The week schedule as last read on this connection; null before it was read, and when the board refused the read, its
/// answer couldn't be read or it gave no answer: never a guess.
/// </param>
public sealed record SmartDenStatus(ulong Relays, SmartDenSchedule? Schedule = null)
{
    /// <summary>
    /// Compares only the relays: a change in the schedule alone isn't a change the safety services must see
    /// (<see cref="BoardPoller{TReading}"/>'s comparer), and comes as the board's own rules changing instead.
    /// </summary>
    public static IEqualityComparer<SmartDenStatus> RelaysComparer { get; } = new RelaysOnly();

    private sealed class RelaysOnly : IEqualityComparer<SmartDenStatus>
    {
        public bool Equals(SmartDenStatus? x, SmartDenStatus? y)
        {
            return x is null ? y is null : y is not null && x.Relays == y.Relays;
        }

        public int GetHashCode(SmartDenStatus obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return obj.Relays.GetHashCode();
        }
    }
}

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record SmartDenState(bool IsConnected, SmartDenStatus? Status, DateTimeOffset? LastContactUtc)
{
    public static SmartDenState Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    /// <summary>The week schedule as last read (<see cref="SmartDenStatus.Schedule"/>); null when not known.</summary>
    public SmartDenSchedule? Schedule => Status?.Schedule;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
