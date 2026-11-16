using System.Buffers.Binary;
using System.Globalization;

namespace Contactor.Boards.Advantech;

/// <summary>
/// The ADAM modules' Modbus TCP frames in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn on
/// relay 3" with "00 07 00 00 00 06 01 05 00 12 FF 00" below it. Modbus has no login, so there is nothing secret to hide.
/// </summary>
public static class AdamTraceText
{
    // The header, the function code, an address and a value or quantity.
    private const int SimpleFrameBytes = AdamApi.HeaderBytes + 5;

    /// <summary>What a request asks for: "Ask for the relays", "Ask for the inputs", "Turn on relay 3", "Turn off relay 3".</summary>
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
            AdamApi.ReadCoils when address == AdamApi.RelayAddress => "Ask for the relays",
            AdamApi.ReadDiscreteInputs when address == AdamApi.InputAddress => "Ask for the inputs",
            AdamApi.WriteSingleCoil when address >= AdamApi.RelayAddress => value switch
            {
                AdamApi.CoilOn => $"Turn on relay {Relay(address)}",
                AdamApi.CoilOff => $"Turn off relay {Relay(address)}",
                _ => $"Write relay {Relay(address)} with {value:X4}",
            },
            AdamApi.WriteMultipleCoils when address >= AdamApi.RelayAddress => $"Set {Number(value)} relays from relay {Relay(address)}",
            _ => $"Function {function:X2} at {address:X4}",
        };
    }

    /// <summary>"Relays: all off", "Relays: 1 and 3 on" or "Relays: all on".</summary>
    public static string Relays(ulong relays, int count)
    {
        return $"Relays: {OnList(relays, count)}";
    }

    /// <summary>"Inputs: all off", "Inputs: 2 on" or "Inputs: all on".</summary>
    public static string Inputs(ulong inputs, int count)
    {
        return $"Inputs: {OnList(inputs, count)}";
    }

    /// <summary>What a switch's answer confirms: "Relay 3 turned on", "Relay 3 turned off".</summary>
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
            AdamApi.WriteSingleCoil when address >= AdamApi.RelayAddress && value == AdamApi.CoilOn => $"Relay {Relay(address)} turned on",
            AdamApi.WriteSingleCoil when address >= AdamApi.RelayAddress && value == AdamApi.CoilOff => $"Relay {Relay(address)} turned off",
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
            AdamApi.IllegalFunction => "unknown function",
            AdamApi.IllegalDataAddress => "no such address",
            AdamApi.IllegalDataValue => "a value it doesn't take",
            AdamApi.DeviceFailure => "it failed",
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

    // The relay at a frame address from RelayAddress on: 1-based.
    private static string Relay(ushort address)
    {
        return Number(address - AdamApi.RelayAddress + 1);
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
