using System.Buffers.Binary;

namespace Contactor.Boards.Advantech;

/// <summary>ADAM-6200 Modbus PDUs for the model's own configurable I/O address map.</summary>
public static class Adam6200Modbus
{
    public const byte ReadCoils = 0x01;
    public const byte WriteSingleCoil = 0x05;
    public const ushort RelayCoilBase = 0x0017;
    public const ushort RelayCount = 6;

    public static byte[] ReadRelays() => [ReadCoils, 0x00, 0x17, 0x00, 0x06];

    public static byte[] WriteRelay(int channel, bool on)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, RelayCount);
        var pdu = new byte[5];
        pdu[0] = WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)(RelayCoilBase + channel - 1));
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), on ? (ushort)0xFF00 : (ushort)0x0000);
        return pdu;
    }

    public static ulong ReadRelayBits(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 3 || pdu[0] != ReadCoils || pdu[1] != 1)
        {
            throw new InvalidDataException("The ADAM-6200 returned an invalid relay response.");
        }

        return (ulong)(pdu[2] & 0x3F);
    }
}
