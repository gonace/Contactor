namespace Contactor.Boards.Vm201;

/// <summary>
/// Reads whole VM201 packets from a stream, one at a time, using <see cref="Vm201PacketDecoder"/>.
/// </summary>
public sealed class Vm201PacketReader
{
    private readonly Stream _stream;
    private readonly Vm201PacketDecoder _decoder;
    private readonly byte[] _buffer = new byte[512];
    private readonly Queue<Vm201Packet> _pending = new();

    public Vm201PacketReader(Stream stream, Vm201PacketDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(decoder);
        _stream = stream;
        _decoder = decoder;
    }

    /// <summary>
    /// Returns the next valid packet. Throws <see cref="EndOfStreamException"/> when the other side closes the
    /// connection.
    /// </summary>
    public async ValueTask<Vm201Packet> ReadAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            var read = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The connection was closed by the other side.");
            }

            foreach (var packet in _decoder.Feed(_buffer.AsSpan(0, read)))
            {
                _pending.Enqueue(packet);
            }
        }

        return _pending.Dequeue();
    }
}
