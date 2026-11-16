using System.Buffers.Binary;

namespace Contactor.Boards.Advantech;

/// <summary>ADAM-6000 Modbus function PDUs for the manual's ADAM-6060/6066 map.</summary>
public static class Adam6000Modbus
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte WriteSingleCoil = 0x05;
    public const ushort RelayCoilBase = 0x0017;
    public const ushort InputBase = 0x0001;
    public const ushort ModelNameRegister = 0x0211;

    public static byte[] Read(byte function, ushort start, ushort count)
    {
        if (function is not (ReadCoils or ReadDiscreteInputs))
        {
            throw new ArgumentOutOfRangeException(nameof(function));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(count, (ushort)1);
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }

    public static byte[] WriteRelay(int channel, bool on)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 6);
        var pdu = new byte[5];
        pdu[0] = WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)(RelayCoilBase + channel - 1));
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), on ? (ushort)0xFF00 : (ushort)0x0000);
        return pdu;
    }

    public static ulong Bits(ReadOnlySpan<byte> pdu, byte function, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        var byteCount = (count + 7) / 8;
        if (pdu.Length != byteCount + 2 || pdu[0] != function || pdu[1] != byteCount)
        {
            throw new InvalidDataException("The module returned an invalid bit response.");
        }

        ulong bits = 0;
        for (var i = 0; i < byteCount; i++)
        {
            bits |= (ulong)pdu[i + 2] << (i * 8);
        }
        return count == 64 ? bits : bits & ((1UL << count) - 1);
    }
}
