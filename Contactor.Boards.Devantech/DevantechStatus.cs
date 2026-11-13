namespace Contactor.Boards.Devantech;

/// <summary>One answer to a poll: which relays and which inputs are on.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per input, bit 0 = input 1; 1 = active. Always 0 without inputs.</param>
public sealed record DevantechStatus(ulong Relays, ulong Inputs);

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record DevantechState(bool IsConnected, DevantechStatus? Status, DateTimeOffset? LastContactUtc)
{
    public static DevantechState Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    public ulong Inputs => Status?.Inputs ?? 0;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
