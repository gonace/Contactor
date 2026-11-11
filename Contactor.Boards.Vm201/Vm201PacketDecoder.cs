using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.Vm201;

/// <summary>
/// Incremental decoder for a VM201 byte stream. TCP may split a packet over several reads or merge several packets
/// into one, so bytes are buffered until a whole frame is there. Invalid bytes (no STX, a bad length, a bad checksum
/// or a missing ETX) are logged and skipped one at a time until the next valid frame; the stream is never given up.
/// </summary>
public sealed class Vm201PacketDecoder
{
    // Holds at most one incomplete frame between calls, plus room for the next chunk.
    private readonly byte[] _buffer = new byte[Vm201PacketCodec.MaxPacketLength * 2];
    private readonly ILogger _logger;
    private int _count;

    public Vm201PacketDecoder()
        : this(NullLogger.Instance)
    {
    }

    public Vm201PacketDecoder(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Raised with the number of bytes skipped as invalid, each time a run of them is skipped.</summary>
    public event EventHandler<int>? Discarded;

    /// <summary>Total number of bytes skipped as invalid since this decoder was created.</summary>
    public long DiscardedByteCount { get; private set; }

    /// <summary>
    /// Adds bytes from the stream and returns every packet that is now complete, in order. An incomplete packet at
    /// the end stays buffered for the next call.
    /// </summary>
    public IReadOnlyList<Vm201Packet> Feed(ReadOnlySpan<byte> bytes)
    {
        List<Vm201Packet> packets = [];
        while (!bytes.IsEmpty)
        {
            // After Drain the buffer holds less than one maximum packet, so there is always room here.
            var take = Math.Min(bytes.Length, _buffer.Length - _count);
            bytes[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            bytes = bytes[take..];
            Drain(packets);
        }

        return packets;
    }

    private void Drain(List<Vm201Packet> packets)
    {
        var start = 0;
        var discarded = 0;
        while (start < _count)
        {
            var stx = _buffer.AsSpan(start, _count - start).IndexOf(Vm201PacketCodec.Stx);
            if (stx < 0)
            {
                discarded += _count - start;
                start = _count;
                break;
            }

            discarded += stx;
            start += stx;
            var available = _count - start;
            if (available < 2)
            {
                break;
            }

            var length = _buffer[start + 1];
            if (!IsPlausibleLength(length))
            {
                discarded++;
                start++;
                continue;
            }

            if (available >= 3)
            {
                var expected = Vm201Command.ExpectedPacketLength(_buffer[start + 2]);
                if (expected is not null && expected != length)
                {
                    discarded++;
                    start++;
                    continue;
                }
            }

            if (available < length)
            {
                break;
            }

            var frame = _buffer.AsSpan(start, length);
            if (frame[^1] != Vm201PacketCodec.Etx || frame[^2] != Vm201PacketCodec.Checksum(frame[..^2]))
            {
                discarded++;
                start++;
                continue;
            }

            packets.Add(new Vm201Packet(frame[2], frame[3..^2].ToArray()));
            start += length;
        }

        if (discarded > 0)
        {
            DiscardedByteCount += discarded;
            _logger.DiscardedBytes(discarded);
            Discarded?.Invoke(this, discarded);
        }

        _buffer.AsSpan(start, _count - start).CopyTo(_buffer);
        _count -= start;
    }

    // The longest packet the protocol defines is a channel name (22 bytes). Rejecting longer lengths stops a stray
    // STX from stalling the stream while the decoder waits for up to 255 bytes that never form a frame.
    private static bool IsPlausibleLength(byte length)
    {
        return length is >= Vm201PacketCodec.FramingLength and <= Vm201Command.MaxPacketLength;
    }
}
