using System.Buffers.Binary;

namespace Contactor.Boards.Advantech;

/// <summary>
/// One Modbus TCP request the app sends, with what it needs to read the answer: its transaction id, unit, function, first
/// address and quantity (or the value it writes, for FC 05).
/// </summary>
/// <param name="Frame">The whole frame as sent, MBAP header included.</param>
/// <param name="TransactionId">The transaction id the answer must carry.</param>
/// <param name="Unit">The unit id the answer must carry.</param>
/// <param name="Function">The function code.</param>
/// <param name="Address">The first address, as in the frame.</param>
/// <param name="Quantity">How many coils or inputs; for FC 05 the value written.</param>
public sealed record AdamRequest(byte[] Frame, ushort TransactionId, byte Unit, byte Function, ushort Address, ushort Quantity)
{
    /// <summary>Whether the answer carries a byte count and data (FC 01, FC 02), rather than an echo.</summary>
    public bool IsRead => Function is AdamApi.ReadCoils or AdamApi.ReadDiscreteInputs;

    /// <summary>The byte count a read's answer must have: a bit per coil or input, rounded up to whole bytes.</summary>
    public int AnswerDataBytes => (Quantity + 7) / 8;
}

/// <summary>
/// Our own small Modbus TCP codec, only for what the app and the simulator use. A frame is the MBAP header (transaction
/// id, protocol id 0, the length of what follows it, unit id) and the PDU (function code and data). Numbers are
/// big-endian. There is no CRC: TCP checks the bytes. An answer is read by its header's length, at most
/// <see cref="AdamApi.MaxFrameBytes"/> bytes, so whatever a module sends can't grow memory.
/// </summary>
public static class AdamModbus
{
    /// <summary>The largest quantity of coils or inputs one read may ask for (Modbus: 2000).</summary>
    public const int MaxReadBits = 2000;

    /// <summary>FC 01: read <paramref name="count"/> relays from relay 1.</summary>
    public static AdamRequest ReadRelays(ushort transaction, int count, byte unit = AdamApi.UnitId)
    {
        return Read(transaction, unit, AdamApi.ReadCoils, AdamApi.RelayAddress, count);
    }

    /// <summary>FC 02: read <paramref name="count"/> digital inputs from input 1.</summary>
    public static AdamRequest ReadInputs(ushort transaction, int count, byte unit = AdamApi.UnitId)
    {
        return Read(transaction, unit, AdamApi.ReadDiscreteInputs, AdamApi.InputAddress, count);
    }

    /// <summary>FC 01 or FC 02: read <paramref name="count"/> bits from the frame address <paramref name="address"/>.</summary>
    public static AdamRequest Read(ushort transaction, byte unit, byte function, ushort address, int count)
    {
        if (function is not (AdamApi.ReadCoils or AdamApi.ReadDiscreteInputs))
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Only FC 01 and FC 02 read bits.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxReadBits);
        return Simple(transaction, unit, function, address, (ushort)count);
    }

    /// <summary>FC 05: switch relay <paramref name="relay"/> (1-based, at most <see cref="ChannelMask.MaxChannels"/>) on (0xFF00) or off (0x0000).</summary>
    public static AdamRequest WriteSingleCoil(ushort transaction, int relay, bool on, byte unit = AdamApi.UnitId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, ChannelMask.MaxChannels);
        return WriteCoil(transaction, unit, RelayCoil(relay), on);
    }

    /// <summary>FC 05 at the frame address <paramref name="address"/>: on (0xFF00) or off (0x0000).</summary>
    public static AdamRequest WriteCoil(ushort transaction, byte unit, ushort address, bool on)
    {
        return Simple(transaction, unit, AdamApi.WriteSingleCoil, address, on ? AdamApi.CoilOn : AdamApi.CoilOff);
    }

    /// <summary>The frame address of relay <paramref name="relay"/> (1-based): <see cref="AdamApi.RelayAddress"/> + relay − 1.</summary>
    public static ushort RelayCoil(int relay)
    {
        return (ushort)(AdamApi.RelayAddress + relay - 1);
    }

    /// <summary>A whole frame: the MBAP header for <paramref name="pdu"/>, then the PDU.</summary>
    public static byte[] Frame(ushort transaction, byte unit, ReadOnlySpan<byte> pdu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pdu.Length, 1, nameof(pdu));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pdu.Length, AdamApi.MaxPduBytes, nameof(pdu));
        var frame = new byte[AdamApi.HeaderBytes + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), AdamApi.ProtocolId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame.AsSpan(AdamApi.HeaderBytes));
        return frame;
    }

    /// <summary>
    /// Reads exactly the answer to <paramref name="request"/> from <paramref name="stream"/>, and checks it: the same
    /// transaction, protocol 0, the same unit and function, a length the header and the request agree on, the byte
    /// count the request gives (a read) or the echo it must be (a write). Returns the whole frame. Throws
    /// <see cref="AdamRefusedException"/> for an exception answer, <see cref="AdamProtocolException"/> for
    /// anything else that isn't the answer, and <see cref="IOException"/> when the stream ends first. Reads at most
    /// <see cref="AdamApi.MaxFrameBytes"/> bytes per frame.
    /// </summary>
    /// <param name="late">
    /// Transaction ids of earlier requests on this connection whose answer nobody waited for: an answer to one of them
    /// may still come first. It is read by its header's length and dropped, and its id taken out, so each is skipped
    /// once at most. Null: none.
    /// </param>
    /// <param name="skipped">Called with each late answer that was dropped, for the trace.</param>
    public static async Task<byte[]> ReadAnswerAsync(
        Stream stream,
        AdamRequest request,
        CancellationToken cancellationToken,
        ICollection<ushort>? late = null,
        Action<byte[]>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        byte[] head;
        ushort transaction;
        ushort protocol;
        ushort length;
        while (true)
        {
            head = new byte[AdamApi.HeaderBytes + 1];
            await ReadExactlyAsync(stream, head, cancellationToken).ConfigureAwait(false);
            transaction = BinaryPrimitives.ReadUInt16BigEndian(head);
            protocol = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
            length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
            if (transaction == request.TransactionId || late is null || !late.Remove(transaction))
            {
                break;
            }

            // The answer to a request given up on: only its length matters, to find the next frame.
            if (protocol != AdamApi.ProtocolId || length is < 2 or > AdamApi.MaxLength)
            {
                throw new AdamProtocolException($"The late answer to transaction {transaction} isn't a Modbus frame.", head);
            }

            var dropped = await ReadRestAsync(stream, head, length - 2, cancellationToken).ConfigureAwait(false);
            skipped?.Invoke(dropped);
        }

        if (transaction != request.TransactionId)
        {
            throw new AdamProtocolException($"The answer is for transaction {transaction}, not {request.TransactionId}.", head);
        }

        if (protocol != AdamApi.ProtocolId)
        {
            throw new AdamProtocolException($"The answer is for protocol {protocol}, not Modbus ({AdamApi.ProtocolId}).", head);
        }

        if (head[6] != request.Unit)
        {
            throw new AdamProtocolException($"The answer came from unit {head[6]}, not {request.Unit}.", head);
        }

        if (length is < 2 or > AdamApi.MaxLength)
        {
            throw new AdamProtocolException($"The answer's length {length} isn't a Modbus length.", head);
        }

        var function = head[7];
        if (function == (request.Function | AdamApi.ExceptionFlag))
        {
            // The unit, the function and the exception code.
            if (length != 3)
            {
                throw new AdamProtocolException($"The exception answer is {length} bytes long, not 3.", head);
            }

            var exception = await ReadRestAsync(stream, head, 1, cancellationToken).ConfigureAwait(false);
            throw new AdamRefusedException(exception[^1], exception);
        }

        if (function != request.Function)
        {
            throw new AdamProtocolException($"The answer is for function {function:X2}, not {request.Function:X2}.", head);
        }

        // A read: the unit, the function, the byte count and the data. A write (FC 05): the unit, the function, then the
        // echo of the address and value.
        var expected = request.IsRead ? 3 + request.AnswerDataBytes : 6;
        if (length != expected)
        {
            throw new AdamProtocolException($"The answer is {length} bytes long, not {expected}.", head);
        }

        var frame = await ReadRestAsync(stream, head, length - 2, cancellationToken).ConfigureAwait(false);
        if (request.IsRead)
        {
            if (frame[8] != request.AnswerDataBytes)
            {
                throw new AdamProtocolException($"The answer has {frame[8]} bytes of data, not {request.AnswerDataBytes}.", frame);
            }

            return frame;
        }

        if (!frame.AsSpan(8, 4).SequenceEqual(request.Frame.AsSpan(8, 4)))
        {
            throw new AdamProtocolException("The answer doesn't confirm what was asked.", frame);
        }

        return frame;
    }

    /// <summary>The bits of a FC 01 or FC 02 answer, bit 0 = the first; at most 64, and none past <paramref name="count"/>.</summary>
    public static ulong Bits(ReadOnlySpan<byte> answer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ChannelMask.MaxChannels);
        var bytes = (count + 7) / 8;
        if (answer.Length != AdamApi.HeaderBytes + 2 + bytes || answer[8] != bytes)
        {
            throw new AdamProtocolException("The answer doesn't carry that many bits.", answer.ToArray());
        }

        ulong bits = 0;
        for (var i = 0; i < bytes; i++)
        {
            bits |= (ulong)answer[AdamApi.HeaderBytes + 2 + i] << (8 * i);
        }

        // Bits past the last one asked for are padding: the module should send zeros, and they mean nothing.
        return count == ChannelMask.MaxChannels ? bits : bits & ((1UL << count) - 1);
    }

    /// <summary>The frame as the Debug screen shows its bytes: "00 01 00 00 00 06 00 05 00 00 FF 00".</summary>
    public static string Hex(ReadOnlySpan<byte> frame)
    {
        return Convert.ToHexString(frame) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : "";
    }

    private static AdamRequest Simple(ushort transaction, byte unit, byte function, ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return new AdamRequest(Frame(transaction, unit, pdu), transaction, unit, function, address, value);
    }

    private static async Task<byte[]> ReadRestAsync(Stream stream, byte[] prefix, int rest, CancellationToken cancellationToken)
    {
        var frame = new byte[prefix.Length + rest];
        prefix.CopyTo(frame, 0);
        await ReadExactlyAsync(stream, frame.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        return frame;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("The module closed the connection in the middle of an answer.", ex);
        }
    }
}
