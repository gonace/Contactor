using System.Globalization;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// One row of the simulated board's week schedule, as its registers hold it (table 10.7): at <see cref="Hour"/>:<see cref="Minute"/>
/// on <see cref="WeekDays"/>, the board sets <see cref="Relays"/> on or off, while the row is enabled.
/// </summary>
/// <param name="Row">The row, 0 to 29.</param>
/// <param name="Enabled">Its enable flag.</param>
/// <param name="Relays">Its outputs code, bit 0 = relay 1.</param>
/// <param name="TurnOn">Its outputs state: true sets the relays on, false off.</param>
/// <param name="Hour">0 to 23.</param>
/// <param name="Minute">0 to 59.</param>
/// <param name="WeekDays">Its weekdays code, bit 0 = Sunday, bit 6 = Saturday.</param>
public sealed record SmartDenSimulatedRow(int Row, bool Enabled, ushort Relays, bool TurnOn, int Hour, int Minute, int WeekDays)
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>What the row does, in words for the page: "Relays 1 and 2 on at 06:30, Mon, Tue".</summary>
    public string Text
    {
        get
        {
            var relays = new ChannelMask(Relays).Channels.Select(r => r.ToString(CultureInfo.InvariantCulture)).ToList();
            var which = relays.Count switch
            {
                0 => "No relay",
                1 => $"Relay {relays[0]}",
                _ => $"Relays {string.Join(", ", relays[..^1])} and {relays[^1]}",
            };
            var days = (WeekDays & 0x7F) switch
            {
                0 => "no day",
                0x7F => "every day",
                var code => string.Join(", ", Enumerable.Range(0, 7).Where(d => (code & (1 << d)) != 0).Select(d => DayNames[d])),
            };
            return string.Create(CultureInfo.InvariantCulture, $"{which} {(TurnOn ? "on" : "off")} at {Hour:00}:{Minute:00}, {days}");
        }
    }
}
