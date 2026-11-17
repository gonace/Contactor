using System.Text;

namespace Contactor.Boards.Devantech;

/// <summary>One command the app sends, with the length of its answer, which the board doesn't frame.</summary>
/// <param name="Bytes">The command as sent, in one write.</param>
/// <param name="AnswerLength">How many bytes the board answers.</param>
public sealed record DevantechRequest(byte[] Bytes, int AnswerLength)
{
    /// <summary>The command byte.</summary>
    public byte Command => Bytes[0];
}

/// <summary>
/// The Devantech boards' binary commands as bytes: what the app and the simulator send and read. Outputs come low byte
/// first and inputs high byte first, as the boards send them; nothing longer than a model's answer is ever read.
/// </summary>
public static class DevantechProtocol
{
    /// <summary>The longest answer the app reads: the ETH1610's 0x34, 2 bytes for each of its 16 analog inputs.</summary>
    public const int MaxAnswerBytes = 32;

    /// <summary>The bytes of one analog value in an answer to 0x32 or 0x34.</summary>
    public const int AnalogValueBytes = 2;

    /// <summary>0x10: id, hardware and firmware.</summary>
    public static DevantechRequest GetModuleInfo()
    {
        return new([DevantechApi.GetModuleInfo], 3);
    }

    /// <summary>0x24: the outputs, <see cref="DevantechLayout.OutputBytes"/> bytes.</summary>
    public static DevantechRequest GetOutputs(DevantechLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new([DevantechApi.GetOutputs], layout.OutputBytes);
    }

    /// <summary>0x25: the inputs, <see cref="DevantechLayout.InputBytes"/> bytes; only for a model with inputs.</summary>
    public static DevantechRequest GetInputs(DevantechLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(layout.InputBytes, 1);
        return new([DevantechApi.GetInputs], layout.InputBytes);
    }

    /// <summary>0x32 and the input's number: one analog input, a 2-byte answer.</summary>
    public static DevantechRequest GetAnalog(int input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, 255);
        return new([DevantechApi.GetAnalog, (byte)input], AnalogValueBytes);
    }

    /// <summary>0x34: all <paramref name="count"/> analog inputs at once, 2 bytes each; only the ETH1610 has it.</summary>
    public static DevantechRequest GetAllAnalog(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxAnswerBytes / AnalogValueBytes);
        return new([DevantechApi.GetAllAnalog], count * AnalogValueBytes);
    }

    /// <summary>0x20 or 0x21: one relay on or off for good (pulse 0), 1 byte answer.</summary>
    public static DevantechRequest Switch(int relay, bool on)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, 255);
        return new([on ? DevantechApi.DigitalActive : DevantechApi.DigitalInactive, (byte)relay, DevantechApi.Permanent], 1);
    }

    /// <summary>
    /// 0x20 with a pulse time: one relay on, and off again by the board after <paramref name="length"/>, 1 byte answer.
    /// Only a length <see cref="DevantechModels.Pulse"/> can do.
    /// </summary>
    public static DevantechRequest Pulse(int relay, TimeSpan length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, 255);
        if (!DevantechModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A Devantech board pulses for 0.1 s to 25.5 s, in steps of 0.1 s.");
        }

        return new([DevantechApi.DigitalActive, (byte)relay, (byte)(length.Ticks / DevantechApi.PulseUnit.Ticks)], 1);
    }

    /// <summary>0x7A: the seconds until the board locks again, 1 byte.</summary>
    public static DevantechRequest GetUnlockTime()
    {
        return new([DevantechApi.GetUnlockTime], 1);
    }

    /// <summary>0x79 and the password's ASCII bytes, 1 byte answer. A secret: never trace or log its bytes.</summary>
    public static DevantechRequest Password(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (DevantechModels.CheckPassword(password) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(password));
        }

        return new([DevantechApi.Password, .. Encoding.ASCII.GetBytes(password)], 1);
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes, the answer to one command. Throws <see cref="IOException"/> when
    /// the board closes the connection first.
    /// </summary>
    public static async Task<byte[]> ReadAnswerAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxAnswerBytes);
        var answer = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(answer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("The board closed the connection in the middle of an answer.", ex);
        }

        return answer;
    }

    /// <summary>The relays in a 0x24 answer, low byte first, bit 0 = relay 1; bits past the model's relays are dropped.</summary>
    public static ulong Outputs(ReadOnlySpan<byte> answer, int relayCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relayCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relayCount, ChannelMask.MaxChannels);
        ulong bits = 0;
        for (var i = 0; i < answer.Length && i < 8; i++)
        {
            bits |= (ulong)answer[i] << (8 * i);
        }

        return bits & ChannelMask.FirstN(relayCount).Bits;
    }

    /// <summary>The inputs in a 0x25 answer, high byte first, bit 0 of the last byte = input 1; a set bit is active.</summary>
    public static ulong Inputs(ReadOnlySpan<byte> answer, int inputCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputCount, ChannelMask.MaxChannels);
        ulong bits = 0;
        for (var i = 0; i < answer.Length && i < 8; i++)
        {
            bits |= (ulong)answer[answer.Length - 1 - i] << (8 * i);
        }

        return bits & ChannelMask.FirstN(inputCount).Bits;
    }

    /// <summary>
    /// The value in a 2-byte answer to 0x32, high byte first: 0 to <see cref="DevantechApi.AnalogMaxCounts"/>. Null for
    /// an answer of another length, or with any of the upper 6 bits set, which the manuals say are 0.
    /// </summary>
    public static int? AnalogValue(ReadOnlySpan<byte> answer)
    {
        if (answer.Length != AnalogValueBytes)
        {
            return null;
        }

        var value = (answer[0] << 8) | answer[1];
        return value <= DevantechApi.AnalogMaxCounts ? value : null;
    }

    /// <summary>
    /// The values in an answer to 0x34, 2 bytes each, high byte first, input 1 first. Null for an answer that isn't
    /// exactly <paramref name="count"/> values, or with a value <see cref="AnalogValue"/> can't read.
    /// </summary>
    public static int[]? AnalogValues(ReadOnlySpan<byte> answer, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (answer.Length != count * AnalogValueBytes)
        {
            return null;
        }

        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (AnalogValue(answer.Slice(i * AnalogValueBytes, AnalogValueBytes)) is not { } value)
            {
                return null;
            }

            values[i] = value;
        }

        return values;
    }

    /// <summary>The 2 bytes of an analog value, high byte first, as 0x32 and 0x34 answer it.</summary>
    public static byte[] AnalogBytes(int counts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(counts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(counts, DevantechApi.AnalogMaxCounts);
        return [(byte)(counts >> 8), (byte)counts];
    }

    /// <summary>The bytes of a 0x24 answer for these relays, low byte first.</summary>
    public static byte[] OutputBytes(ulong relays, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte)(relays >> (8 * i));
        }

        return bytes;
    }

    /// <summary>The bytes of a 0x25 answer for these inputs, high byte first.</summary>
    public static byte[] InputBytes(ulong inputs, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[count - 1 - i] = (byte)(inputs >> (8 * i));
        }

        return bytes;
    }

    /// <summary>The bytes as the Debug screen shows them: "20 08 00".</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : "";
    }
}
