namespace Contactor.Boards.Advantech;

/// <summary>One answer to a poll: which relays and digital inputs are on.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per digital input, bit 0 = input 1; 1 = the module's DI value is 1. None on a module without inputs.</param>
public sealed record AdamStatus(ulong Relays, ulong Inputs);

/// <summary>
/// The module's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The module answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the module last answered a poll, or null if it never has.</param>
public sealed record AdamState(bool IsConnected, AdamStatus? Status, DateTimeOffset? LastContactUtc)
{
    public static AdamState Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    public ulong Inputs => Status?.Inputs ?? 0;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
