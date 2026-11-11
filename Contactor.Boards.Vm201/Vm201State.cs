namespace Contactor.Boards.Vm201;

/// <summary>
/// The board's last reported status. The board is the source of truth: outputs are never stored by the app. While
/// disconnected, the last known values are kept with <see cref="IsConnected"/> false, so the UI can show them with a
/// warning.
/// </summary>
/// <param name="IsConnected">Logged in and the board has reported its status on the current connection.</param>
/// <param name="Outputs">Bit per channel, bit 0 = channel 1; 1 = on.</param>
/// <param name="TimerMask">Bit per channel; 1 = the board's own timer program is active.</param>
/// <param name="Input">The board's input; true = on.</param>
/// <param name="LastContactUtc">When the board last reported its status, or null if it never has.</param>
public sealed record Vm201State(
    bool IsConnected,
    byte Outputs,
    byte TimerMask,
    bool Input,
    DateTimeOffset? LastContactUtc)
{
    public static Vm201State Initial { get; } = new(false, 0, 0, false, null);

    public bool IsOn(int channel)
    {
        return (Outputs & Vm201Channel.Mask(channel)) != 0;
    }
}

/// <summary>
/// Channel numbers and masks. Channels are numbered 1–8; bit 0 of a mask is channel 1.
/// </summary>
public static class Vm201Channel
{
    public const int Count = 8;

    public const byte All = 0xFF;

    public static byte Mask(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, Count);
        return (byte)(1 << (channel - 1));
    }
}
