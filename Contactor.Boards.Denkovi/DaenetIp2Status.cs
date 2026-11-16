namespace Contactor.Boards.Denkovi;

/// <summary>One answer to a poll: which relays are on.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on (its pin is High).</param>
public sealed record DaenetIp2Status(ulong Relays);

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
