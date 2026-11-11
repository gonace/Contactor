namespace Contactor.Boards.Vm201;

/// <summary>What happened on the board connection.</summary>
public enum Vm201TraceKind
{
    /// <summary>A packet to the board.</summary>
    Sent,

    /// <summary>A packet from the board.</summary>
    Received,

    /// <summary>Connecting; <see cref="Vm201TraceEntry.Detail"/> is "host:port".</summary>
    Connecting,

    /// <summary>The connection ended; <see cref="Vm201TraceEntry.Detail"/> is why.</summary>
    ConnectionLost,

    /// <summary>Waiting before the next attempt; <see cref="Vm201TraceEntry.Detail"/> is the wait in whole seconds.</summary>
    Reconnecting,

    /// <summary>Invalid bytes from the board were skipped; <see cref="Vm201TraceEntry.Detail"/> is how many.</summary>
    DiscardedBytes,
}

/// <summary>
/// One step on the board connection, as <see cref="Vm201Client.Traced"/> reports it.
/// </summary>
/// <param name="At">When it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Frame">
/// The whole packet for <see cref="Vm201TraceKind.Sent"/> and <see cref="Vm201TraceKind.Received"/>, otherwise empty.
/// A user name or password packet holds only its first three bytes (STX, LEN and the command): the credentials never
/// leave the client.
/// </param>
/// <param name="Detail">More about a connection step, see <see cref="Vm201TraceKind"/>; null for packets.</param>
public sealed record Vm201TraceEntry(DateTimeOffset At, Vm201TraceKind Kind, ReadOnlyMemory<byte> Frame, string? Detail)
{
    /// <summary>The packet's command byte, or 0 when there is no packet.</summary>
    public byte Command => Frame.Length >= 3 ? Frame.Span[2] : (byte)0;

    /// <summary>The packet's data bytes (between the command and the checksum); empty when hidden or not a packet.</summary>
    public ReadOnlySpan<byte> Data => Frame.Length >= 5 ? Frame.Span[3..^2] : [];

    /// <summary>A packet to trace: a copy, cut to its header when it holds a user name or password.</summary>
    internal static ReadOnlyMemory<byte> Redact(ReadOnlySpan<byte> frame)
    {
        var hidden = frame.Length >= 3 && frame[2] is Vm201Command.Username or Vm201Command.Password;
        return hidden ? frame[..3].ToArray() : frame.ToArray();
    }
}
