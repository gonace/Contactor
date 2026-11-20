using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Denkovi;

namespace Contactor.Simulator;

/// <summary>
/// The page's controls for a smartDEN's week schedule, as on the board's own web page (section 8.9): each row on or
/// off, a row to add (one or two relays, on or off, every day at a time), and the app's reads and writes of it refused.
/// Each change is a line "at the board" in the traffic. Nothing changes while the power is cut.
/// </summary>
public sealed class SmartDenControls
{
    public const string On = "on";
    public const string Off = "off";

    private readonly SmartDenSimulator _board;
    private readonly Action<string> _log;

    public SmartDenControls(SmartDenSimulator board, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(log);
        _board = board;
        _log = log;
    }

    /// <summary>The rows that are on or have relays.</summary>
    public IReadOnlyList<SmartDenSimulatedRow> Rows => _board.ScheduleRows;

    /// <summary>Whether the app's reads of the schedule are refused.</summary>
    public bool RefuseReads => _board.RefuseScheduleReads;

    /// <summary>Whether the app's writes to the schedule are refused.</summary>
    public bool RefuseWrites => _board.RefuseScheduleWrites;

    /// <summary>The new row's first relay.</summary>
    public int Relay { get; private set; } = 1;

    /// <summary>The new row's second relay; 0 for none.</summary>
    public int AlsoRelay { get; private set; }

    /// <summary>"on" or "off".</summary>
    public string State { get; private set; } = On;

    public string? Time { get; set; } = "08:00";

    public string? Error { get; private set; }

    /// <summary>Whether a row is free for a new one.</summary>
    public bool CanAdd => _board.FreeRow is not null;

    public void SetRelay(string? value)
    {
        if (Number(value) is { } relay and >= 1 and <= SmartDenApi.RelayCount)
        {
            Relay = relay;
        }
    }

    public void SetAlsoRelay(string? value)
    {
        if (Number(value) is { } relay and >= 0 and <= SmartDenApi.RelayCount)
        {
            AlsoRelay = relay;
        }
    }

    public void SetState(string? value)
    {
        if (value is On or Off)
        {
            State = value;
        }
    }

    public Task SetEnabledAsync(int row, bool enabled)
    {
        if (_board.IsPoweredOff)
        {
            return Task.CompletedTask;
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"Week schedule row {row} turned {(enabled ? "on" : "off")} here"));
        return _board.SetRowEnabledAsync(row, enabled);
    }

    /// <summary>Adds the row typed, every day, in the first free row; says why not in <see cref="Error"/>.</summary>
    public async Task AddAsync()
    {
        Error = null;
        if (_board.IsPoweredOff)
        {
            return;
        }

        if (!TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            Error = "Give a time as HH:mm.";
            return;
        }

        if (_board.FreeRow is not { } row)
        {
            Error = "All 30 rows are in use.";
            return;
        }

        var relays = ChannelMask.Of(AlsoRelay == 0 ? [Relay] : [Relay, AlsoRelay]);
        var added = new SmartDenSimulatedRow(row, Enabled: true, (ushort)relays.Bits, State == On, time.Hour, time.Minute, WeekDays: 0x7F);
        _log($"Week schedule row {row.ToString(CultureInfo.InvariantCulture)} added here: {added.Text}");
        await _board.SetRowAsync(added).ConfigureAwait(false);
    }

    public void SetRefuseReads(bool refuse)
    {
        if (_board.RefuseScheduleReads == refuse)
        {
            return;
        }

        _board.RefuseScheduleReads = refuse;
        _log(refuse ? "Reads of the week schedule refused here" : "Reads of the week schedule answered again here");
    }

    public void SetRefuseWrites(bool refuse)
    {
        if (_board.RefuseScheduleWrites == refuse)
        {
            return;
        }

        _board.RefuseScheduleWrites = refuse;
        _log(refuse ? "Writes to the week schedule refused here" : "Writes to the week schedule taken again here");
    }

    private static int? Number(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
