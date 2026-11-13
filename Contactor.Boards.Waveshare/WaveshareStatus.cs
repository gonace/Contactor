namespace Contactor.Boards.Waveshare;

/// <summary>One answer to a poll: which relays and which inputs are on.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per input, bit 0 = input 1; 1 = on as the board reads it. Always 0 without inputs.</param>
public sealed record WaveshareStatus(ulong Relays, ulong Inputs);

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record WaveshareState(bool IsConnected, WaveshareStatus? Status, DateTimeOffset? LastContactUtc)
{
    public static WaveshareState Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    public ulong Inputs => Status?.Inputs ?? 0;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}

/// <summary>The relays the client set to normal mode on connecting, and the modes it found.</summary>
/// <param name="Modes">The mode of relay 1 to 8 before, e.g. 1 for linkage; at least one wasn't 0 (normal).</param>
/// <param name="Endpoint">The board it was done on.</param>
public sealed record WaveshareRelayModesReset(IReadOnlyList<ushort> Modes, BoardEndpoint Endpoint);
