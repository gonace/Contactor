using System.Numerics;

namespace Contactor.Boards;

/// <summary>
/// A set of a board's channels (relays or inputs), numbered from 1. Bit 0 is channel 1; a board has at most
/// <see cref="MaxChannels"/> of a kind.
/// </summary>
public readonly record struct ChannelMask(ulong Bits)
{
    public const int MaxChannels = 64;

    public static ChannelMask None => default;

    public bool IsEmpty => Bits == 0;

    /// <summary>How many channels are in the set.</summary>
    public int Count => BitOperations.PopCount(Bits);

    /// <summary>The channels in the set, lowest first.</summary>
    public IEnumerable<int> Channels
    {
        get
        {
            var bits = Bits;
            while (bits != 0)
            {
                var index = BitOperations.TrailingZeroCount(bits);
                yield return index + 1;
                bits &= bits - 1;
            }
        }
    }

    /// <summary>One channel, 1 to <see cref="MaxChannels"/>.</summary>
    public static ChannelMask Of(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, MaxChannels);
        return new ChannelMask(1UL << (channel - 1));
    }

    /// <summary>The given channels.</summary>
    public static ChannelMask Of(IEnumerable<int> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels.Aggregate(None, (mask, channel) => mask | Of(channel));
    }

    /// <summary>Channels 1 to <paramref name="count"/>: all of a board's channels of a kind.</summary>
    public static ChannelMask FirstN(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxChannels);
        return new ChannelMask(count == MaxChannels ? ulong.MaxValue : (1UL << count) - 1);
    }

    public bool Contains(int channel)
    {
        return channel is >= 1 and <= MaxChannels && (Bits & (1UL << (channel - 1))) != 0;
    }

    /// <summary>Whether every channel in <paramref name="other"/> is in this set.</summary>
    public bool ContainsAll(ChannelMask other)
    {
        return (Bits & other.Bits) == other.Bits;
    }

    /// <summary>Whether any channel in <paramref name="other"/> is in this set.</summary>
    public bool Overlaps(ChannelMask other)
    {
        return (Bits & other.Bits) != 0;
    }

    /// <summary>This set without the channels in <paramref name="other"/>.</summary>
    public ChannelMask Except(ChannelMask other)
    {
        return new ChannelMask(Bits & ~other.Bits);
    }

    public static ChannelMask operator |(ChannelMask left, ChannelMask right) => new(left.Bits | right.Bits);

    public static ChannelMask operator &(ChannelMask left, ChannelMask right) => new(left.Bits & right.Bits);

    public static ChannelMask BitwiseOr(ChannelMask left, ChannelMask right) => left | right;

    public static ChannelMask BitwiseAnd(ChannelMask left, ChannelMask right) => left & right;

    public override string ToString()
    {
        return IsEmpty ? "none" : string.Join(",", Channels);
    }
}
