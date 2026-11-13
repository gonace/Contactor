using System.Buffers.Binary;
using System.Globalization;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// The Waveshare board's Modbus frames in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn
/// on relay 3" with "01 05 00 02 FF 00 2D FA" below it. Modbus has no login, so there is nothing secret to hide.
/// </summary>
public static class WaveshareTraceText
{
    /// <summary>
    /// What a request asks for: "Ask for the relays", "Turn on relay 3", "Pulse relay 3 for 0.7 s", "Set every relay to
    /// normal mode".
    /// </summary>
    public static string Request(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8)
        {
            return "A frame too short to be a request";
        }

        var function = frame[1];
        var address = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
        return function switch
        {
            WaveshareApi.ReadCoils => "Ask for the relays",
            WaveshareApi.ReadDiscreteInputs => "Ask for the inputs",
            WaveshareApi.ReadHoldingRegisters when address == WaveshareApi.RelayModeRegister => "Ask for the relay modes",
            WaveshareApi.WriteSingleCoil when WaveshareModbus.IsFlashOn(address) =>
                $"Pulse relay {Number(address - WaveshareApi.FlashOnAddress + 1)} for {Seconds(value)}",
            WaveshareApi.WriteSingleCoil => value switch
            {
                WaveshareApi.CoilOn => $"Turn on relay {Number(address + 1)}",
                WaveshareApi.CoilOff => $"Turn off relay {Number(address + 1)}",
                WaveshareApi.CoilToggle => $"Flip relay {Number(address + 1)}",
                _ => $"Write coil {Number(address + 1)} with {value:X4}",
            },
            WaveshareApi.WriteSingleRegister when IsModeRegister(address) =>
                $"Set relay {Number(address - WaveshareApi.RelayModeRegister + 1)} to {ModeName(value)} mode",
            WaveshareApi.WriteMultipleRegisters when address == WaveshareApi.RelayModeRegister && AllZero(frame[7..^2]) => "Set every relay to normal mode",
            WaveshareApi.WriteMultipleRegisters when address == WaveshareApi.RelayModeRegister => "Set the relay modes",
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

    /// <summary>"Relay modes: all normal", or the ones that aren't: "Relay modes: 3 linkage, 5 toggle".</summary>
    public static string Modes(IReadOnlyList<ushort> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var special = modes.Select((mode, index) => (mode, relay: index + 1)).Where(m => m.mode != WaveshareApi.ModeNormal).ToList();
        return special.Count == 0
            ? "Relay modes: all normal"
            : $"Relay modes: {string.Join(", ", special.Select(m => $"{Number(m.relay)} {ModeName(m.mode)}"))}";
    }

    /// <summary>What a write's answer confirms: "Relay 3 turned on", "Relay 3 turned on for 0.7 s", "Relay modes written".</summary>
    public static string Confirmed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8)
        {
            return "Done";
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
        return frame[1] switch
        {
            WaveshareApi.WriteSingleCoil when WaveshareModbus.IsFlashOn(address) =>
                $"Relay {Number(address - WaveshareApi.FlashOnAddress + 1)} turned on for {Seconds(value)}",
            WaveshareApi.WriteSingleCoil when value == WaveshareApi.CoilOn => $"Relay {Number(address + 1)} turned on",
            WaveshareApi.WriteSingleCoil when value == WaveshareApi.CoilOff => $"Relay {Number(address + 1)} turned off",
            WaveshareApi.WriteSingleCoil when value == WaveshareApi.CoilToggle => $"Relay {Number(address + 1)} flipped",
            WaveshareApi.WriteSingleRegister or WaveshareApi.WriteMultipleRegisters when IsModeRegister(address) => "Relay modes written",
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
            WaveshareApi.IllegalFunction => "unknown function",
            WaveshareApi.IllegalDataAddress => "no such address",
            WaveshareApi.IllegalDataValue => "a value it doesn't take",
            WaveshareApi.DeviceFailure => "it failed",
            _ => "exception",
        };
        return $"{name} (code {Number(code)})";
    }

    /// <summary>A relay control mode in words: "normal", "linkage", "toggle", "edge".</summary>
    public static string ModeName(ushort mode)
    {
        return mode switch
        {
            WaveshareApi.ModeNormal => "normal",
            WaveshareApi.ModeLinkage => "linkage",
            WaveshareApi.ModeToggle => "toggle",
            WaveshareApi.ModeEdge => "edge",
            _ => $"unknown ({Number(mode)})",
        };
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

    // A flash time in 100 ms units: "0.7 s", "3276.7 s".
    private static string Seconds(ushort time)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{time / 10.0:0.0} s");
    }

    private static bool IsModeRegister(ushort address)
    {
        return address is >= WaveshareApi.RelayModeRegister and < WaveshareApi.RelayModeRegister + WaveshareApi.RelayModeCount;
    }

    private static bool AllZero(ReadOnlySpan<byte> data)
    {
        return data.IndexOfAnyExcept((byte)0) < 0;
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
