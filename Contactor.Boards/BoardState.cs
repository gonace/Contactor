namespace Contactor.Boards;

/// <summary>
/// A board's last reported state. The board is the source of truth: the app never stores which relays are on. While
/// disconnected, the last known values are kept with <see cref="IsConnected"/> false, so the screens can show them
/// with a warning.
/// </summary>
/// <param name="IsConnected">Connected, and the board has reported its state on the current connection.</param>
/// <param name="Relays">The relays that are on.</param>
/// <param name="Inputs">The digital inputs that are on.</param>
/// <param name="LastContactUtc">When the board last reported its state, or null if it never has.</param>
public sealed record BoardState(bool IsConnected, ChannelMask Relays, ChannelMask Inputs, DateTimeOffset? LastContactUtc)
{
    public static BoardState Initial { get; } = new(false, ChannelMask.None, ChannelMask.None, null);

    /// <summary>Whether a relay is on, 1 being the first.</summary>
    public bool IsOn(int relay)
    {
        return Relays.Contains(relay);
    }
}
