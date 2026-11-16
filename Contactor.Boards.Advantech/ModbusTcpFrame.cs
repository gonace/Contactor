using System.Buffers.Binary;

namespace Contactor.Boards.Advantech;

/// <summary>A Modbus TCP ADU: the seven-byte MBAP header followed by a bounded PDU.</summary>
public static class ModbusTcpFrame
{
    public const int HeaderLength = 7;
    public const int MaxPduLength = 253;

    public static byte[] Encode(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pdu.Length, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pdu.Length, MaxPduLength);
        var frame = new byte[HeaderLength + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static int GetFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
        {
            throw new ArgumentException("An MBAP header has 7 bytes.", nameof(header));
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(header[2..]) != 0)
        {
            throw new InvalidDataException("The Modbus TCP protocol id must be zero.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (length is < 3 or > MaxPduLength + 1)
        {
            throw new InvalidDataException($"The Modbus TCP length {length} is outside the allowed range.");
        }

        return HeaderLength + length - 1;
    }

    public static void ValidateAnswer(ReadOnlySpan<byte> answer, ushort transactionId, byte unitId, byte function)
    {
        if (answer.Length < HeaderLength + 2 || GetFrameLength(answer) != answer.Length)
        {
            throw new InvalidDataException("The Modbus TCP answer has an invalid length.");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(answer) != transactionId || answer[6] != unitId)
        {
            throw new InvalidDataException("The Modbus TCP answer has the wrong transaction or unit id.");
        }

        var received = answer[HeaderLength];
        if (received != function && received != (function | 0x80))
        {
            throw new InvalidDataException("The Modbus TCP answer has the wrong function code.");
        }

        if (received == (function | 0x80))
        {
            throw new ModbusExceptionResponseException(function, answer[HeaderLength + 1]);
        }
    }
}

/// <summary>A valid Modbus exception response: the request arrived, but the module rejected it.</summary>
public sealed class ModbusExceptionResponseException(byte function, byte exceptionCode)
    : Exception($"The module refused function 0x{function:X2} with Modbus exception 0x{exceptionCode:X2}.")
{
    public byte Function { get; } = function;
    public byte ExceptionCode { get; } = exceptionCode;
}
