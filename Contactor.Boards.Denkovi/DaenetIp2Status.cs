namespace Contactor.Boards.Denkovi;

/// <summary>One answer to a poll: which relays are on, what the analog inputs of P6 read, and the restart pins as last read.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on (its pin is High).</param>
/// <param name="Analog">
/// What each analog input read, 0 to 1023, input 1 first; null before the first poll read them. Every input is null
/// ("no reading") after an analog answer that failed, and for a minute after it
/// (<see cref="DaenetIp2Client.AnalogRetryInterval"/>).
/// </param>
/// <param name="RestartPins">
/// The ICMP monitoring's restart pins, <c>cfgP5DupRST</c> (bit 0 = P5.1), as last read on this connection; null before
/// they were read, on a board without them (no such name, as a firmware older than 4.097 answers), and when the board
/// refused the read, its answer couldn't be read or it gave no answer: never a guess.
/// </param>
public sealed record DaenetIp2Status(ulong Relays, AnalogReadings? Analog = null, int? RestartPins = null)
{
    /// <summary>
    /// Compares the relays only: an analog value moves a little on every reading, so it alone is no state change, which
    /// would wake the safety services. It comes as <see cref="DaenetIp2Client.AnalogChanged"/> instead, and a change in
    /// the restart pins alone as <see cref="DaenetIp2Client.OwnRulesChanged"/>.
    /// </summary>
    public static IEqualityComparer<DaenetIp2Status> SwitchesComparer { get; } = new Switches();

    private sealed class Switches : IEqualityComparer<DaenetIp2Status>
    {
        public bool Equals(DaenetIp2Status? x, DaenetIp2Status? y)
        {
            return ReferenceEquals(x, y) || (x is not null && y is not null && x.Relays == y.Relays);
        }

        public int GetHashCode(DaenetIp2Status obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return obj.Relays.GetHashCode();
        }
    }
}

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll since the client last (re)started.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record DaenetIp2State(bool IsConnected, DaenetIp2Status? Status, DateTimeOffset? LastContactUtc)
{
    public static DaenetIp2State Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
