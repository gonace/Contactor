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
