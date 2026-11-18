namespace Contactor.Boards.Waveshare;

/// <summary>One answer to a poll: which relays and which inputs are on, and on the (B) and (C) the relays' control modes.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per input, bit 0 = input 1; 1 = on as the board reads it. Always 0 without inputs.</param>
/// <param name="Modes">
/// The control mode of relay 1 to 8 (<see cref="WaveshareApi.ModeNormal"/> to <see cref="WaveshareApi.ModeEdge"/>) as
/// last read on this connection; null on a model without them, before they were read, and when the board refused the
/// read or its answer couldn't be read: never a guess.
/// </param>
public sealed record WaveshareStatus(ulong Relays, ulong Inputs, IReadOnlyList<ushort>? Modes = null)
{
    /// <summary>
    /// Compares only the relays and the inputs: a change in the modes alone isn't a change the safety services must
    /// see (<see cref="BoardPoller{TReading}"/>'s comparer), and comes as the board's own rules changing instead.
    /// </summary>
    public static IEqualityComparer<WaveshareStatus> SwitchesComparer { get; } = new Switches();

    public bool Equals(WaveshareStatus? other)
    {
        return other is not null
            && Relays == other.Relays
            && Inputs == other.Inputs
            && (Modes is null ? other.Modes is null : other.Modes is not null && Modes.SequenceEqual(other.Modes));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Relays);
        hash.Add(Inputs);
        foreach (var mode in Modes ?? [])
        {
            hash.Add(mode);
        }

        return hash.ToHashCode();
    }

    private sealed class Switches : IEqualityComparer<WaveshareStatus>
    {
        public bool Equals(WaveshareStatus? x, WaveshareStatus? y)
        {
            return x is null ? y is null : y is not null && x.Relays == y.Relays && x.Inputs == y.Inputs;
        }

        public int GetHashCode(WaveshareStatus obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return HashCode.Combine(obj.Relays, obj.Inputs);
        }
    }
}

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

    /// <summary>The relays' control modes as last read (<see cref="WaveshareStatus.Modes"/>); null when not known.</summary>
    public IReadOnlyList<ushort>? Modes => Status?.Modes;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
