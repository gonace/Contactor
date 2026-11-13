using System.Buffers.Binary;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// One Modbus RTU request the app sends, with what it needs to read the answer: its function, its first address and
/// its quantity (or the value it writes, for FC 05 and FC 06).
/// </summary>
/// <param name="Frame">The whole frame as sent, CRC included.</param>
/// <param name="Function">The function code.</param>
/// <param name="Address">The first address.</param>
/// <param name="Quantity">How many coils, inputs or registers; for FC 05 and FC 06 the value written.</param>
public sealed record WaveshareRequest(byte[] Frame, byte Function, ushort Address, ushort Quantity)
{
    /// <summary>Whether the answer carries a byte count and data (FC 01, 02, 03), rather than an echo.</summary>
    public bool IsRead => Function is WaveshareApi.ReadCoils or WaveshareApi.ReadDiscreteInputs or WaveshareApi.ReadHoldingRegisters;

    /// <summary>The byte count a read's answer must have: a bit per coil or input, two bytes per register.</summary>
    public int AnswerDataBytes => Function == WaveshareApi.ReadHoldingRegisters ? Quantity * 2 : (Quantity + 7) / 8;
}

/// <summary>
/// Our own small Modbus RTU codec, only for what the app and the simulator use. A frame is the unit id, the function
/// code, its data and a CRC-16/Modbus (polynomial 0xA001 reflected, start 0xFFFF), low byte first. Numbers in the
/// data are big-endian. Over TCP the frames come without the MBAP header ("Modbus RTU over TCP"), so an answer's length
/// follows from the request it answers; nothing longer than <see cref="WaveshareApi.MaxFrameBytes"/> is ever read.
/// </summary>
public static class WaveshareModbus
{
    /// <summary>The largest quantity of coils or inputs one read may ask for (Modbus: 2000).</summary>
    public const int MaxReadBits = 2000;

    /// <summary>The largest quantity of registers one read may ask for (Modbus: 125).</summary>
    public const int MaxReadRegisters = 125;

    /// <summary>The largest quantity of registers one write may carry (Modbus: 123).</summary>
    public const int MaxWriteRegisters = 123;

    /// <summary>The CRC-16/Modbus of <paramref name="data"/>; sent low byte first.</summary>
    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    /// <summary>Whether the frame's last two bytes are the CRC of the rest.</summary>
    public static bool HasValidCrc(ReadOnlySpan<byte> frame)
    {
        return frame.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]) == Crc(frame[..^2]);
    }

    /// <summary>The frame with its CRC added.</summary>
    public static byte[] WithCrc(ReadOnlySpan<byte> body)
    {
        var frame = new byte[body.Length + 2];
        body.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(body.Length), Crc(body));
        return frame;
    }

    /// <summary>FC 01: read <paramref name="count"/> relays from <paramref name="address"/>.</summary>
    public static WaveshareRequest ReadCoils(byte unit, ushort address, ushort count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)MaxReadBits);
        return Simple(unit, WaveshareApi.ReadCoils, address, count);
    }

    /// <summary>FC 02: read <paramref name="count"/> digital inputs from <paramref name="address"/>.</summary>
    public static WaveshareRequest ReadDiscreteInputs(byte unit, ushort address, ushort count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)MaxReadBits);
        return Simple(unit, WaveshareApi.ReadDiscreteInputs, address, count);
    }

    /// <summary>FC 03: read <paramref name="count"/> holding registers from <paramref name="address"/>.</summary>
    public static WaveshareRequest ReadHoldingRegisters(byte unit, ushort address, ushort count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)MaxReadRegisters);
        return Simple(unit, WaveshareApi.ReadHoldingRegisters, address, count);
    }

    /// <summary>FC 05: switch the relay at <paramref name="address"/> on (0xFF00) or off (0x0000).</summary>
    public static WaveshareRequest WriteSingleCoil(byte unit, ushort address, bool on)
    {
        return Simple(unit, WaveshareApi.WriteSingleCoil, address, on ? WaveshareApi.CoilOn : WaveshareApi.CoilOff);
    }

    /// <summary>FC 10: write <paramref name="values"/> to the holding registers from <paramref name="address"/>.</summary>
    public static WaveshareRequest WriteMultipleRegisters(byte unit, ushort address, ReadOnlySpan<ushort> values)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Length, MaxWriteRegisters);
        var body = new byte[7 + (values.Length * 2)];
        body[0] = unit;
        body[1] = WaveshareApi.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), (ushort)values.Length);
        body[6] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(7 + (i * 2)), values[i]);
        }

        return new WaveshareRequest(WithCrc(body), WaveshareApi.WriteMultipleRegisters, address, (ushort)values.Length);
    }

    /// <summary>
    /// Reads exactly the answer to <paramref name="request"/> from <paramref name="stream"/>, and checks it: the same
    /// unit and function, the byte count the request gives (a read) or the echo it must be (a write), and the CRC.
    /// Returns the whole frame. Throws <see cref="WaveshareRefusedException"/> for an exception answer,
    /// <see cref="WaveshareProtocolException"/> for anything else that isn't the answer, and <see cref="IOException"/>
    /// when the stream ends first. Reads at most <see cref="WaveshareApi.MaxFrameBytes"/> bytes, so whatever a board
    /// sends can't grow memory.
    /// </summary>
    public static async Task<byte[]> ReadAnswerAsync(Stream stream, WaveshareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        var unit = request.Frame[0];
        var head = new byte[2];
        await ReadExactlyAsync(stream, head, cancellationToken).ConfigureAwait(false);
        if (head[0] != unit)
        {
            throw new WaveshareProtocolException($"The answer came from unit {head[0]}, not {unit}.", head);
        }

        if (head[1] == (request.Function | WaveshareApi.ExceptionFlag))
        {
            var exception = await ReadRestAsync(stream, head, 3, cancellationToken).ConfigureAwait(false);
            CheckCrc(exception);
            throw new WaveshareRefusedException(exception[2], exception);
        }

        if (head[1] != request.Function)
        {
            throw new WaveshareProtocolException($"The answer is for function {head[1]:X2}, not {request.Function:X2}.", head);
        }

        if (request.IsRead)
        {
            var countByte = new byte[1];
            await ReadExactlyAsync(stream, countByte, cancellationToken).ConfigureAwait(false);
            byte[] prefix = [head[0], head[1], countByte[0]];
            if (countByte[0] != request.AnswerDataBytes)
            {
                throw new WaveshareProtocolException($"The answer has {countByte[0]} bytes of data, not {request.AnswerDataBytes}.", prefix);
            }

            var read = await ReadRestAsync(stream, prefix, countByte[0] + 2, cancellationToken).ConfigureAwait(false);
            CheckCrc(read);
            return read;
        }

        // FC 05 and FC 06 echo the request; FC 0F and FC 10 answer the address and quantity.
        var echo = await ReadRestAsync(stream, head, 6, cancellationToken).ConfigureAwait(false);
        CheckCrc(echo);
        if (!echo.AsSpan(0, 6).SequenceEqual(request.Frame.AsSpan(0, 6)))
        {
            throw new WaveshareProtocolException("The answer doesn't confirm what was asked.", echo);
        }

        return echo;
    }

    /// <summary>The bits of a FC 01 or FC 02 answer, bit 0 = the first coil or input; at most 64.</summary>
    public static ulong Bits(ReadOnlySpan<byte> answer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ChannelMask.MaxChannels);
        var bytes = (count + 7) / 8;
        if (answer.Length != 5 + bytes || answer[2] != bytes)
        {
            throw new WaveshareProtocolException("The answer doesn't carry that many bits.", answer.ToArray());
        }

        ulong bits = 0;
        for (var i = 0; i < bytes; i++)
        {
            bits |= (ulong)answer[3 + i] << (8 * i);
        }

        // Bits past the last coil are padding: the board should send zeros, and they mean nothing.
        return count == ChannelMask.MaxChannels ? bits : bits & ((1UL << count) - 1);
    }

    /// <summary>The registers of a FC 03 answer.</summary>
    public static ushort[] Registers(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 5 || answer[2] % 2 != 0 || answer.Length != 5 + answer[2])
        {
            throw new WaveshareProtocolException("The answer doesn't carry whole registers.", answer.ToArray());
        }

        var registers = new ushort[answer[2] / 2];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(answer[(3 + (i * 2))..]);
        }

        return registers;
    }

    /// <summary>The frame as the Debug screen shows its bytes: "01 05 00 00 FF 00 8C 3A".</summary>
    public static string Hex(ReadOnlySpan<byte> frame)
    {
        return Convert.ToHexString(frame) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : "";
    }

    private static WaveshareRequest Simple(byte unit, byte function, ushort address, ushort value)
    {
        var body = new byte[6];
        body[0] = unit;
        body[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), value);
        return new WaveshareRequest(WithCrc(body), function, address, value);
    }

    private static void CheckCrc(byte[] frame)
    {
        if (!HasValidCrc(frame))
        {
            throw new WaveshareProtocolException("The answer's CRC is wrong.", frame);
        }
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
