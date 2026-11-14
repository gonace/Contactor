using System.Globalization;

namespace Contactor.Boards.Devantech;

/// <summary>
/// The Devantech board's commands and answers in plain words, for the app's Debug screen and the simulator alike, e.g.
/// "Turn on relay 3" with "20 03 00" below it. The password never shows: <see cref="HiddenPassword"/> stands for its
/// bytes.
/// </summary>
public static class DevantechTraceText
{
    /// <summary>The bytes of a password command as the trace shows them: the command byte only.</summary>
    public const string HiddenPassword = "79 …";

    /// <summary>What a command asks for: "Ask for the relays", "Turn on relay 3", "Send the password".</summary>
    public static string Request(ReadOnlySpan<byte> command)
    {
        if (command.IsEmpty)
        {
            return "An empty command";
        }

        return command[0] switch
        {
            DevantechApi.GetModuleInfo => "Ask which board it is",
            DevantechApi.GetOutputs => "Ask for the relays",
            DevantechApi.GetInputs => "Ask for the inputs",
            DevantechApi.GetUnlockTime => "Ask whether the board is locked",
            DevantechApi.Password => "Send the password",
            DevantechApi.DigitalActive when command.Length >= 3 => Switch("Turn on", command[1], command[2]),
            DevantechApi.DigitalInactive when command.Length >= 3 => Switch("Turn off", command[1], command[2]),
            var other => $"Command {other:X2}",
        };
    }

    /// <summary>The bytes of a command as the trace shows them, the password hidden.</summary>
    public static string RequestBytes(ReadOnlySpan<byte> command)
    {
        return !command.IsEmpty && command[0] == DevantechApi.Password ? HiddenPassword : DevantechProtocol.Hex(command);
    }

    /// <summary>"Board: ETH008, hardware 2, firmware 28".</summary>
    public static string Module(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 3)
        {
            return "Board: unknown";
        }

        var name = DevantechModels.ModuleName(answer[0]) ?? $"module {Number(answer[0])}";
        return $"Board: {name}, hardware {Number(answer[1])}, firmware {Number(answer[2])}";
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

    /// <summary>"Locked", "No password", "Unlocked for 30 s".</summary>
    public static string UnlockTime(byte answer)
    {
        return answer switch
        {
            DevantechApi.Locked => "Locked: the board needs the password",
            DevantechApi.NoPassword => "No password",
            <= DevantechApi.UnlockSeconds => $"Unlocked for {Number(answer)} s",
            _ => $"Unknown lock state {answer:X2}",
        };
    }

    /// <summary>"Password accepted" or "Password refused".</summary>
    public static string PasswordAnswer(byte answer)
    {
        return answer switch
        {
            DevantechApi.PasswordAccepted => "Password accepted",
            DevantechApi.PasswordRefused => "Password refused",
            _ => $"Unknown answer {answer:X2} to the password",
        };
    }

    /// <summary>
    /// What the answer to a relay command says: "Relay 3 turned on", "Relay 3 turned on for 0.5 s", "Refused: relay 3".
    /// </summary>
    public static string Switched(ReadOnlySpan<byte> command, byte answer)
    {
        var relay = command.Length >= 2 ? Number(command[1]) : "?";
        var on = !command.IsEmpty && command[0] == DevantechApi.DigitalActive;
        var pulse = command.Length >= 3 && command[2] != DevantechApi.Permanent ? $" for {Seconds(command[2])}" : "";
        return answer switch
        {
            DevantechApi.Success => $"Relay {relay} turned {(on ? "on" : "off")}{pulse}",
            DevantechApi.Failure => $"Refused: relay {relay}",
            _ => $"Unknown answer {answer:X2} for relay {relay}",
        };
    }

    private static string Switch(string what, byte relay, byte pulse)
    {
        return pulse == DevantechApi.Permanent
            ? $"{what} relay {Number(relay)}"
            : $"{what} relay {Number(relay)} for {Seconds(pulse)}";
    }

    // A pulse time in 100 ms units: "0.5 s", "25.5 s".
    private static string Seconds(byte pulse)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{pulse / 10.0:0.0} s");
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

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
