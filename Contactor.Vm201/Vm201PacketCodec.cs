using System.Text;

namespace Contactor.Vm201;

/// <summary>
/// A decoded VM201 packet: the command byte and its data, without framing.
/// </summary>
public readonly record struct Vm201Packet(byte Command, ReadOnlyMemory<byte> Data);

/// <summary>
/// Encodes VM201 packets: <c>&lt;STX&gt;&lt;LEN&gt;&lt;CMD&gt;&lt;data…&gt;&lt;CHECKSUM&gt;&lt;ETX&gt;</c>.
/// LEN counts every byte including STX and ETX. CHECKSUM is the two's complement of the sum of all bytes before it.
/// </summary>
public static class Vm201PacketCodec
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;

    /// <summary>STX, LEN, CMD, CHECKSUM and ETX.</summary>
    public const int FramingLength = 5;

    /// <summary>LEN is one byte, so a packet can never be longer than this.</summary>
    public const int MaxPacketLength = byte.MaxValue;

    public const int MaxDataLength = MaxPacketLength - FramingLength;

    public static byte[] Encode(byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxDataLength)
        {
            throw new ArgumentException($"A VM201 packet carries at most {MaxDataLength} data bytes.", nameof(data));
        }

        var length = data.Length + FramingLength;
        var packet = new byte[length];
        packet[0] = Stx;
        packet[1] = (byte)length;
        packet[2] = command;
        data.CopyTo(packet.AsSpan(3));
        packet[length - 2] = Checksum(packet.AsSpan(0, length - 2));
        packet[length - 1] = Etx;
        return packet;
    }

    public static byte[] Encode(byte command, byte mask)
    {
        return Encode(command, [mask]);
    }

    public static byte[] Encode(byte command)
    {
        return Encode(command, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// The two's complement of the sum of the bytes, modulo 256.
    /// </summary>
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        return (byte)(-sum & 0xFF);
    }

    /// <summary>
    /// Encodes a user name or password as 9 zero-padded ASCII bytes. Throws if it is longer than 8 characters or not
    /// printable ASCII, since the board cannot represent anything else.
    /// </summary>
    public static byte[] EncodeCredential(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > Vm201Command.CredentialMaxLength)
        {
            throw new ArgumentException(
                $"A VM201 user name or password has at most {Vm201Command.CredentialMaxLength} characters.",
                nameof(value));
        }

        foreach (var c in value)
        {
            if (c is < (char)0x20 or > (char)0x7E)
            {
                throw new ArgumentException("A VM201 user name or password must be printable ASCII.", nameof(value));
            }
        }

        var data = new byte[Vm201Command.CredentialDataLength];
        Encoding.ASCII.GetBytes(value, data);
        return data;
    }

    /// <summary>
    /// Decodes a zero-padded ASCII field (credential or channel name), stopping at the first zero byte.
    /// </summary>
    public static string DecodeText(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0)
        {
            data = data[..end];
        }

        return Encoding.ASCII.GetString(data);
    }
}
