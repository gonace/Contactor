using System.Buffers.Binary;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// One Modbus TCP request the app sends, with what it needs to read the answer: its transaction id, unit, function, first
/// address and quantity (or the value it writes, for FC 05 and FC 06).
/// </summary>
/// <param name="Frame">The whole frame as sent, MBAP header included.</param>
/// <param name="TransactionId">The transaction id the answer must carry.</param>
/// <param name="Unit">The unit id the answer must carry.</param>
/// <param name="Function">The function code.</param>
/// <param name="Address">The first address.</param>
/// <param name="Quantity">How many coils (FC 01) or registers (FC 03); for FC 05 and FC 06 the value written.</param>
public sealed record SmartDenRequest(byte[] Frame, ushort TransactionId, byte Unit, byte Function, ushort Address, ushort Quantity)
{
    /// <summary>Whether the answer carries a byte count and data (FC 01, FC 03), rather than an echo.</summary>
    public bool IsRead => Function is SmartDenApi.ReadCoils or SmartDenApi.ReadHoldingRegisters;

    /// <summary>
    /// The byte count a read's answer must have: for FC 01 a bit per coil, rounded up to whole bytes; for FC 03 two
    /// bytes per register (table 10.22).
    /// </summary>
    public int AnswerDataBytes => Function == SmartDenApi.ReadHoldingRegisters ? Quantity * 2 : (Quantity + 7) / 8;
}

/// <summary>
/// Our own small Modbus TCP codec, only for what the app and the simulator use. A frame is the MBAP header (transaction
/// id, protocol id 0, the length of what follows it, unit id) and the PDU (function code and data). Numbers are
/// big-endian. There is no CRC: TCP checks the bytes. An answer is read by its header's length, at most
/// <see cref="SmartDenApi.MaxFrameBytes"/> bytes, so whatever a board sends can't grow memory.
/// </summary>
public static class SmartDenModbus
{
    /// <summary>The largest quantity of coils one read may ask for (Modbus: 2000).</summary>
    public const int MaxReadBits = 2000;

    /// <summary>FC 01: read <paramref name="count"/> relays from <paramref name="address"/>.</summary>
    public static SmartDenRequest ReadCoils(ushort transaction, ushort address, ushort count, byte unit = SmartDenApi.UnitId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)MaxReadBits);
        return Simple(transaction, unit, SmartDenApi.ReadCoils, address, count);
    }

    /// <summary>
    /// FC 03: read <paramref name="count"/> holding registers from <paramref name="address"/> (section 10.2.1.3, table
    /// 10.21). The board refuses a read past the end of the block it starts in.
    /// </summary>
    public static SmartDenRequest ReadHoldingRegisters(ushort transaction, ushort address, ushort count, byte unit = SmartDenApi.UnitId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)SmartDenApi.MaxReadRegisters);
        return Simple(transaction, unit, SmartDenApi.ReadHoldingRegisters, address, count);
    }

    /// <summary>
    /// FC 06 at the enable flag of week schedule row <paramref name="row"/> (0 to 29): 0 turns the row off, 1 on (tables
    /// 10.4 and 10.7). Only this one register: the row's relays, state and time stay as they are.
    /// </summary>
    public static SmartDenRequest WriteRowEnable(ushort transaction, int row, bool enable, byte unit = SmartDenApi.UnitId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, SmartDenApi.ScheduleRows);
        return Simple(
            transaction,
            unit,
            SmartDenApi.WriteSingleRegister,
            (ushort)(SmartDenApi.RowEnableRegister + row),
            enable ? SmartDenApi.RowEnabled : SmartDenApi.RowDisabled);
    }

    /// <summary>Whether a register address is a row's enable flag.</summary>
    public static bool IsRowEnable(ushort address)
    {
        return address is >= SmartDenApi.RowEnableRegister and < SmartDenApi.RowEnableRegister + SmartDenApi.ScheduleRows;
    }

    /// <summary>The registers of an FC 03 answer, in order: two bytes each, big-endian (table 10.22).</summary>
    public static ushort[] Registers(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < SmartDenApi.HeaderBytes + 2
            || answer[SmartDenApi.HeaderBytes] != SmartDenApi.ReadHoldingRegisters
            || answer[8] % 2 != 0
            || answer.Length != SmartDenApi.HeaderBytes + 2 + answer[8])
        {
            throw new SmartDenProtocolException("The answer doesn't carry whole registers.", answer.ToArray());
        }

        var registers = new ushort[answer[8] / 2];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(answer[(SmartDenApi.HeaderBytes + 2 + (2 * i))..]);
        }

        return registers;
    }

    /// <summary>FC 05: switch relay <paramref name="relay"/> (1-based) on (0xFF00) or off (0x0000).</summary>
    public static SmartDenRequest WriteSingleCoil(ushort transaction, int relay, bool on, byte unit = SmartDenApi.UnitId)
    {
        CheckRelay(relay);
        return Simple(transaction, unit, SmartDenApi.WriteSingleCoil, (ushort)(relay - 1), on ? SmartDenApi.CoilOn : SmartDenApi.CoilOff);
    }

    /// <summary>
    /// FC 06 at the pulse register of <paramref name="relay"/> (1-based): the board turns it on, and off again by
    /// itself after <paramref name="length"/>, and echoes the request. Only a length <see cref="SmartDenModels.Pulse"/>
    /// can do.
    /// </summary>
    public static SmartDenRequest Pulse(ushort transaction, int relay, TimeSpan length, byte unit = SmartDenApi.UnitId)
    {
        CheckRelay(relay);
        if (!SmartDenModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, PulseRange);
        }

        var time = (ushort)(length.Ticks / SmartDenApi.PulseUnit.Ticks);
        return Simple(transaction, unit, SmartDenApi.WriteSingleRegister, (ushort)(SmartDenApi.PulseRegister + relay - 1), time);
    }

    /// <summary>What <see cref="ArgumentOutOfRangeException"/> says for a pulse length the board can't do.</summary>
    public const string PulseRange = "The smartDEN pulses for 0.1 s to 6553.5 s, in steps of 0.1 s.";

    /// <summary>Whether an FC 06 address is a relay's pulse register.</summary>
    public static bool IsPulse(ushort address)
    {
        return address is >= SmartDenApi.PulseRegister and < SmartDenApi.PulseRegister + SmartDenApi.RelayCount;
    }

    /// <summary>A whole frame: the MBAP header for <paramref name="pdu"/>, then the PDU.</summary>
    public static byte[] Frame(ushort transaction, byte unit, ReadOnlySpan<byte> pdu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pdu.Length, 1, nameof(pdu));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pdu.Length, SmartDenApi.MaxPduBytes, nameof(pdu));
        var frame = new byte[SmartDenApi.HeaderBytes + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), SmartDenApi.ProtocolId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame.AsSpan(SmartDenApi.HeaderBytes));
        return frame;
    }

    /// <summary>
    /// Reads exactly the answer to <paramref name="request"/> from <paramref name="stream"/>, and checks it: the same
    /// transaction, protocol 0, the same unit and function, a length the header and the request agree on, the byte
    /// count the request gives (a read) or the echo it must be (a write). Returns the whole frame. Throws
    /// <see cref="SmartDenRefusedException"/> for an exception answer, <see cref="SmartDenProtocolException"/> for
    /// anything else that isn't the answer, and <see cref="IOException"/> when the stream ends first. Reads at most
    /// <see cref="SmartDenApi.MaxFrameBytes"/> bytes per frame.
    /// </summary>
    /// <param name="late">
    /// Transaction ids of earlier requests on this connection whose answer nobody waited for: an answer to one of them
    /// may still come first. It is read by its header's length and dropped, and its id taken out, so each is skipped
    /// once at most. Null: none.
    /// </param>
    /// <param name="skipped">Called with each late answer that was dropped, for the trace.</param>
    public static async Task<byte[]> ReadAnswerAsync(
        Stream stream,
        SmartDenRequest request,
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
            head = new byte[SmartDenApi.HeaderBytes + 1];
            await ReadExactlyAsync(stream, head, cancellationToken).ConfigureAwait(false);
            transaction = BinaryPrimitives.ReadUInt16BigEndian(head);
            protocol = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
            length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
            if (transaction == request.TransactionId || late is null || !late.Remove(transaction))
            {
                break;
            }

            // The answer to a request given up on: only its length matters, to find the next frame.
            if (protocol != SmartDenApi.ProtocolId || length is < 2 or > SmartDenApi.MaxLength)
            {
                throw new SmartDenProtocolException($"The late answer to transaction {transaction} isn't a Modbus frame.", head);
            }

            var dropped = await ReadRestAsync(stream, head, length - 2, cancellationToken).ConfigureAwait(false);
            skipped?.Invoke(dropped);
        }

        if (transaction != request.TransactionId)
        {
            throw new SmartDenProtocolException($"The answer is for transaction {transaction}, not {request.TransactionId}.", head);
        }

        if (protocol != SmartDenApi.ProtocolId)
        {
            throw new SmartDenProtocolException($"The answer is for protocol {protocol}, not Modbus ({SmartDenApi.ProtocolId}).", head);
        }

        if (head[6] != request.Unit)
        {
            throw new SmartDenProtocolException($"The answer came from unit {head[6]}, not {request.Unit}.", head);
        }

        if (length is < 2 or > SmartDenApi.MaxLength)
        {
            throw new SmartDenProtocolException($"The answer's length {length} isn't a Modbus length.", head);
        }

        var function = head[7];
        if (function == (request.Function | SmartDenApi.ExceptionFlag))
        {
            // The unit, the function and the exception code.
            if (length != 3)
            {
                throw new SmartDenProtocolException($"The exception answer is {length} bytes long, not 3.", head);
            }

            var exception = await ReadRestAsync(stream, head, 1, cancellationToken).ConfigureAwait(false);
            throw new SmartDenRefusedException(exception[^1], exception);
        }

        if (function != request.Function)
        {
            throw new SmartDenProtocolException($"The answer is for function {function:X2}, not {request.Function:X2}.", head);
        }

        // A read: the unit, the function, the byte count and the data. A write: the unit, the function, then FC 05 and
        // FC 06 echo the address and value, FC 0F and FC 10 the address and quantity.
        var expected = request.IsRead ? 3 + request.AnswerDataBytes : 6;
        if (length != expected)
        {
            throw new SmartDenProtocolException($"The answer is {length} bytes long, not {expected}.", head);
        }

        var frame = await ReadRestAsync(stream, head, length - 2, cancellationToken).ConfigureAwait(false);
        if (request.IsRead)
        {
            if (frame[8] != request.AnswerDataBytes)
            {
                throw new SmartDenProtocolException($"The answer has {frame[8]} bytes of data, not {request.AnswerDataBytes}.", frame);
            }

            return frame;
        }

        if (!frame.AsSpan(8, 4).SequenceEqual(request.Frame.AsSpan(8, 4)))
        {
            throw new SmartDenProtocolException("The answer doesn't confirm what was asked.", frame);
        }

        return frame;
    }

    /// <summary>The bits of a FC 01 answer, bit 0 = the first coil; at most 64, and none past <paramref name="count"/>.</summary>
    public static ulong Bits(ReadOnlySpan<byte> answer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ChannelMask.MaxChannels);
        var bytes = (count + 7) / 8;
        if (answer.Length != SmartDenApi.HeaderBytes + 2 + bytes || answer[8] != bytes)
        {
            throw new SmartDenProtocolException("The answer doesn't carry that many bits.", answer.ToArray());
        }

        ulong bits = 0;
        for (var i = 0; i < bytes; i++)
        {
            bits |= (ulong)answer[SmartDenApi.HeaderBytes + 2 + i] << (8 * i);
        }

        // Bits past the last coil are padding: the board should send zeros, and they mean nothing.
        return count == ChannelMask.MaxChannels ? bits : bits & ((1UL << count) - 1);
    }

    /// <summary>The frame as the Debug screen shows its bytes: "00 01 00 00 00 06 00 05 00 00 FF 00".</summary>
    public static string Hex(ReadOnlySpan<byte> frame)
    {
        return Convert.ToHexString(frame) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : "";
    }

    private static SmartDenRequest Simple(ushort transaction, byte unit, byte function, ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return new SmartDenRequest(Frame(transaction, unit, pdu), transaction, unit, function, address, value);
    }

    private static void CheckRelay(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, SmartDenApi.RelayCount);
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
            throw new IOException("The board closed the connection in the middle of an answer.", ex);
        }
    }
}
