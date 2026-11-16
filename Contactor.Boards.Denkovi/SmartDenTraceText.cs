using System.Buffers.Binary;
using System.Globalization;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// The smartDEN's Modbus TCP frames in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn on
/// relay 3" with "00 07 00 00 00 06 00 05 00 02 FF 00" below it. Modbus has no login, so there is nothing secret to hide.
/// </summary>
public static class SmartDenTraceText
{
    // The header, the function code, an address and a value or quantity.
    private const int SimpleFrameBytes = SmartDenApi.HeaderBytes + 5;

    /// <summary>
    /// What a request asks for: "Ask for the relays", "Turn on relay 3", "Turn off relay 3", "Pulse relay 3 for 0.7 s".
    /// </summary>
    public static string Request(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < SimpleFrameBytes)
        {
            return "A frame too short to be a request";
        }

        var function = frame[7];
        var address = BinaryPrimitives.ReadUInt16BigEndian(frame[8..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(frame[10..]);
        return function switch
        {
            SmartDenApi.ReadCoils => "Ask for the relays",
            SmartDenApi.WriteSingleCoil => value switch
            {
                SmartDenApi.CoilOn => $"Turn on relay {Number(address + 1)}",
                SmartDenApi.CoilOff => $"Turn off relay {Number(address + 1)}",
                SmartDenApi.CoilToggle => $"Flip relay {Number(address + 1)}",
                _ => $"Write coil {Number(address + 1)} with {value:X4}",
            },
            SmartDenApi.WriteSingleRegister when SmartDenModbus.IsPulse(address) =>
                $"Pulse relay {Number(address - SmartDenApi.PulseRegister + 1)} for {Seconds(value)}",
            SmartDenApi.WriteMultipleCoils => $"Set {Number(value)} relays from relay {Number(address + 1)}",
            _ => $"Function {function:X2} at {address:X4}",
        };
    }

    /// <summary>"Relays: all off", "Relays: 1 and 3 on" or "Relays: all on".</summary>
    public static string Relays(ulong relays, int count)
    {
        return $"Relays: {OnList(relays, count)}";
    }

    /// <summary>What a write's answer confirms: "Relay 3 turned on", "Relay 3 turned on for 0.7 s".</summary>
    public static string Confirmed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < SimpleFrameBytes)
        {
            return "Done";
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(frame[8..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(frame[10..]);
        return frame[7] switch
        {
            SmartDenApi.WriteSingleCoil when value == SmartDenApi.CoilOn => $"Relay {Number(address + 1)} turned on",
            SmartDenApi.WriteSingleCoil when value == SmartDenApi.CoilOff => $"Relay {Number(address + 1)} turned off",
            SmartDenApi.WriteSingleCoil when value == SmartDenApi.CoilToggle => $"Relay {Number(address + 1)} flipped",
            SmartDenApi.WriteSingleRegister when SmartDenModbus.IsPulse(address) =>
                $"Relay {Number(address - SmartDenApi.PulseRegister + 1)} turned on for {Seconds(value)}",
            _ => "Done",
        };
    }

    /// <summary>"Refused: no such address (code 2)".</summary>
    public static string Refused(byte code)
    {
        return $"Refused: {ExceptionName(code)}";
    }

    /// <summary>A Modbus exception code in words: "no such address (code 2)".</summary>
    public static string ExceptionName(byte code)
    {
        var name = code switch
        {
            SmartDenApi.IllegalFunction => "unknown function",
            SmartDenApi.IllegalDataAddress => "no such address",
            SmartDenApi.IllegalDataValue => "a value it doesn't take",
            SmartDenApi.DeviceFailure => "it failed",
            _ => "exception",
        };
        return $"{name} (code {Number(code)})";
    }

    // "all off", "1 and 3 on", "all on".
    private static string OnList(ulong bits, int count)
    {
        var all = ChannelMask.FirstN(count).Bits;
        bits &= all;
        if (bits == 0)
        {
            return "all off";
        }

        if (bits == all && count > 1)
        {
            return "all on";
        }

        var channels = new ChannelMask(bits).Channels.Select(Number).ToList();
        return channels.Count == 1
            ? $"{channels[0]} on"
            : $"{string.Join(", ", channels[..^1])} and {channels[^1]} on";
    }

    // A pulse's length in 100 ms units: "0.7 s", "6553.5 s".
    private static string Seconds(ushort time)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{time / 10.0:0.0} s");
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
