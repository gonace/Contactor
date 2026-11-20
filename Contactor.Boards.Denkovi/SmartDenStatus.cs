namespace Contactor.Boards.Denkovi;

/// <summary>One row of the board's week schedule that switches relays: enabled, with at least one relay in its outputs code.</summary>
/// <param name="Row">The row's number, 0 to 29, the low byte of its registers' addresses (table 10.7).</param>
/// <param name="Relays">Its outputs code: the relays it switches, bit 0 = relay 1, bit 15 = relay 16.</param>
public readonly record struct SmartDenRow(int Row, ushort Relays);

/// <summary>
/// The board's week schedule as far as the app reads it: the rows that are enabled and switch at least one relay, in row
/// order. A row disabled, or with no relay, switches nothing and isn't here.
/// </summary>
public sealed record SmartDenSchedule(IReadOnlyList<SmartDenRow> Rows)
{
    /// <summary>A schedule with no row that switches anything.</summary>
    public static SmartDenSchedule Empty { get; } = new([]);

    /// <summary>The rows from the two blocks as read: each row's enable flag and its outputs code, row 0 first.</summary>
    public static SmartDenSchedule From(IReadOnlyList<ushort> enabled, IReadOnlyList<ushort> outputs)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(outputs);
        List<SmartDenRow> rows = [];
        for (var row = 0; row < enabled.Count && row < outputs.Count; row++)
        {
            if (enabled[row] == SmartDenApi.RowEnabled && outputs[row] != 0)
            {
                rows.Add(new SmartDenRow(row, outputs[row]));
            }
        }

        return new SmartDenSchedule(rows);
    }

    /// <summary>The rows that switch <paramref name="relay"/>, 1 being the first, in row order.</summary>
    public IReadOnlyList<SmartDenRow> Switching(int relay)
    {
        return relay is >= 1 and <= SmartDenApi.RelayCount ? [.. Rows.Where(r => (r.Relays & (1 << (relay - 1))) != 0)] : [];
    }

    public bool Equals(SmartDenSchedule? other)
    {
        return other is not null && Rows.SequenceEqual(other.Rows);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var row in Rows)
        {
            hash.Add(row);
        }

        return hash.ToHashCode();
    }
}

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
