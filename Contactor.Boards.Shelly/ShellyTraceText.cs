using System.Globalization;
using System.Text;

namespace Contactor.Boards.Shelly;

/// <summary>
/// The Shelly's requests and answers in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn on
/// relay 3" with "POST /rpc {…}" below it. The password and the digest headers never show: they aren't in the body.
/// </summary>
public static class ShellyTraceText
{
    // Enough for any answer but a status, which is cut; nobody reads 3 kB of status on a phone.
    private const int MaxShownLength = 200;

    /// <summary>What a request asks for: "Ask which device it is", "Ask for status", "Turn on relay 3".</summary>
    public static string Request(string method, int relay = 0, bool on = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method switch
        {
            ShellyApi.GetDeviceInfo => "Ask which device it is",
            ShellyApi.GetStatus => "Ask for status",
            ShellyApi.SwitchSet when relay > 0 => string.Create(CultureInfo.InvariantCulture, $"Turn {(on ? "on" : "off")} relay {relay}"),
            ShellyApi.SwitchGetConfig when relay > 0 => string.Create(CultureInfo.InvariantCulture, $"Ask for relay {relay}'s settings"),
            ShellyApi.ScheduleList => "Ask for the schedules",
            ShellyApi.ListMethods => "Ask which methods it has",
            _ => $"Call {Shorten(Printable(method))}",
        };
    }

    /// <summary>A request as it goes on the wire, without its headers: "POST /rpc {"id":3,…}".</summary>
    public static string RequestBytes(ReadOnlySpan<byte> frame, bool withDigest)
    {
        var text = $"POST {ShellyApi.RpcPath} {Shorten(Printable(Encoding.UTF8.GetString(frame)))}";
        return withDigest ? text + " · with the password's digest" : text;
    }

    /// <summary>
    /// "Status: relay 1 and 3 on", "Status: all off", and the inputs of a device that has some: "Status: relay 2 on ·
    /// input 1 on", "Status: all off · inputs off", "Status: all off · input 1 unknown · input 2 off".
    /// </summary>
    public static string Status(ShellyStatus status, int relayCount)
    {
        ArgumentNullException.ThrowIfNull(status);
        var relays = status.Relays == 0 ? "Status: all off" : $"Status: {Channels(status.Relays, relayCount)} on";
        if (status.InputCount == 0)
        {
            return relays;
        }

        if (status.UnknownInputs != 0)
        {
            relays += $" · {InputList(status.UnknownInputs, status.InputCount)} unknown";
        }

        if (status.Inputs == 0 && status.UnknownInputs == 0)
        {
            return relays + (status.InputCount == 1 ? " · input off" : " · inputs off");
        }

        if (status.Inputs == 0)
        {
            // Only the inputs that are known are off: "input 1 unknown · input 2 off", or nothing more when none is.
            var known = (byte)(((1 << status.InputCount) - 1) & ~status.UnknownInputs);
            return known == 0 ? relays : $"{relays} · {InputList(known, status.InputCount)} off";
        }

        return $"{relays} · {InputList(status.Inputs, status.InputCount)} on";
    }

    // "input 1", "input 1 and 3".
    private static string InputList(byte mask, int count)
    {
        var inputs = Enumerable.Range(1, count)
            .Where(input => (mask & (1 << (input - 1))) != 0)
            .Select(input => input.ToString(CultureInfo.InvariantCulture))
            .ToList();
        return inputs.Count == 1
            ? $"input {inputs[0]}"
            : $"input {string.Join(", ", inputs[..^1])} and {inputs[^1]}";
    }

    /// <summary>"Device SNSW-102P16EU, generation 2, firmware 1.7.1, profile switch".</summary>
    public static string DeviceInfo(ShellyDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var text = new StringBuilder("Device ").Append(Shorten(Printable(info.Model ?? info.Id)));
        text.Append(info.Generation is { } gen ? string.Create(CultureInfo.InvariantCulture, $", generation {gen}") : ", no generation");
        text.Append(", firmware ").Append(Shorten(Printable(info.Firmware ?? "unknown")));
        if (info.Profile is { } profile)
        {
            text.Append(", profile ").Append(Shorten(Printable(profile)));
        }

        return text.ToString();
    }

    /// <summary>"Relay 2 is on (it was off)".</summary>
    public static string Switched(int relay, bool on, bool? wasOn)
    {
        var now = on ? "on" : "off";
        var before = wasOn switch
        {
            true => " (it was on)",
            false => " (it was off)",
            null => "",
        };
        return string.Create(CultureInfo.InvariantCulture, $"Relay {relay} is {now}{before}");
    }

    /// <summary>A request to set a relay's state after a power cut: "Set relay 2 after a power cut: as before".</summary>
    public static string SetPowerOn(int relay, PowerOnState state)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Set relay {relay} after a power cut: {PowerOn(state)}");
    }

    /// <summary>A relay's state after a power cut as read: "Relay 2 after a power cut: as before", or "unknown".</summary>
    public static string PowerOnRead(int relay, PowerOnState? state)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Relay {relay} after a power cut: {PowerOn(state)}");
    }

    /// <summary>
    /// A relay's settings as read: "Relay 2 after a power cut: off", and " · turns on again by itself" with its auto-on
    /// on, or " · auto-on unknown" when it couldn't be read; then " · its input switches it at each change",
    /// " · a press on its input switches it", or " · input mode unknown", from <paramref name="input"/>.
    /// </summary>
    public static string SettingsRead(int relay, PowerOnState? state, bool? autoOn, ShellyInputRule? input)
    {
        var suffix = autoOn switch
        {
            true => " · turns on again by itself",
            false => "",
            null => " · auto-on unknown",
        };
        var inputSuffix = input?.Kind switch
        {
            _ when input is null => " · input mode unknown",
            _ when input.Value.Cycle => " · a press on its input steps relays 1 and 2 through on and off",
            OwnRuleKind.InputEdge => " · its input switches it at each change",
            OwnRuleKind.InputToggle => " · a press on its input switches it",
            _ => "",
        };
        return PowerOnRead(relay, state) + suffix + inputSuffix;
    }

    /// <summary>A request to detach one relay's input: "Stop relay 2's input switching it".</summary>
    public static string DetachInput(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Stop relay {relay}'s input switching it");
    }

    /// <summary>The device took it: "Relay 2's input no longer switches it".</summary>
    public static string InputDetached(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s input no longer switches it");
    }

    /// <summary>"Schedules: none switches a relay", "Schedules: 2 switch relay 1 and 3", from the enabled ones.</summary>
    public static string Schedules(ShellySchedules schedules, int relayCount)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var switching = schedules.Jobs.Count(j => j.Enabled && j.Relays != 0);
        return switching == 0
            ? "Schedules: none switches a relay"
            : string.Create(CultureInfo.InvariantCulture, $"Schedules: {switching} {(switching == 1 ? "switches" : "switch")} {Channels(schedules.Relays, relayCount)}");
    }

    /// <summary>What <c>Shelly.ListMethods</c> said of the schedules: "Methods: has schedules", "Methods: has no schedules".</summary>
    public static string Methods(bool hasSchedules)
    {
        return hasSchedules ? "Methods: has schedules" : "Methods: has no schedules";
    }

    /// <summary>A <c>Shelly.ListMethods</c> that failed: "Methods not read (…): whether it has schedules is unknown".</summary>
    public static string MethodsNotRead(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return $"Methods not read ({Shorten(Printable(reason))}): whether it has schedules is unknown";
    }

    /// <summary>A schedules read that failed: "Schedules not read: the device's own rules are unknown".</summary>
    public static string SchedulesNotRead(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return $"Schedules not read ({Shorten(Printable(reason))}): the device's own rules are unknown";
    }

    /// <summary>A request to turn one schedule off: "Turn off schedule 3".</summary>
    public static string TurnOffSchedule(long job)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Turn off schedule {job}");
    }

    /// <summary>The device took it: "Schedule 3 is off".</summary>
    public static string ScheduleOff(long job)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Schedule {job} is off");
    }

    /// <summary>A request to turn one relay's auto-on off: "Turn off relay 2's auto-on".</summary>
    public static string TurnOffAutoOn(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Turn off relay {relay}'s auto-on");
    }

    /// <summary>The device took it: "Relay 2's auto-on is off".</summary>
    public static string AutoOnOff(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s auto-on is off");
    }

    /// <summary>A settings read that stopped at a relay: "Settings not read from relay 2 on: unknown after a power cut".</summary>
    public static string PowerOnReadStopped(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Settings not read from relay {relay} on: unknown after a power cut");
    }

    /// <summary>The device took a setting: "Relay 2's setting saved".</summary>
    public static string SettingSaved(int relay)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s setting saved");
    }

    /// <summary>"off", "as before", "on", "follows its switch", or "unknown" for null.</summary>
    public static string PowerOn(PowerOnState? state)
    {
        return state switch
        {
            PowerOnState.Off => "off",
            PowerOnState.AsBefore => "as before",
            PowerOnState.On => "on",
            PowerOnState.FollowsInput => "follows its switch",
            _ => "unknown",
        };
    }

    /// <summary>An answer's bytes as text: "200 {…}", with anything unprintable as "·", cut at 200 characters.</summary>
    public static string Answer(int statusCode, string? body)
    {
        var code = statusCode.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(body) ? code : $"{code} {Shorten(Printable(body))}";
    }

    /// <summary>"all", "relay 3" or "relay 1, 3 and 4".</summary>
    public static string Channels(byte mask, int count)
    {
        if (count > 1 && mask == (1 << count) - 1)
        {
            return "all";
        }

        if (mask == 0)
        {
            return "no relays";
        }

        List<string> channels = [];
        for (var channel = 1; channel <= 8; channel++)
        {
            if ((mask & (1 << (channel - 1))) != 0)
            {
                channels.Add(channel.ToString(CultureInfo.InvariantCulture));
            }
        }

        return channels.Count == 1
            ? $"relay {channels[0]}"
            : $"relay {string.Join(", ", channels[..^1])} and {channels[^1]}";
    }

    private static string Printable(string text)
    {
        return new string([.. text.Select(c => c is >= ' ' and <= '~' ? c : '·')]);
    }

    private static string Shorten(string text)
    {
        return text.Length <= MaxShownLength ? text : text[..MaxShownLength] + "…";
    }
}
