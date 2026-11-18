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
            _ => $"Call {Shorten(Printable(method))}",
        };
    }

    /// <summary>A request as it goes on the wire, without its headers: "POST /rpc {"id":3,…}".</summary>
    public static string RequestBytes(ReadOnlySpan<byte> frame, bool withDigest)
    {
        var text = $"POST {ShellyApi.RpcPath} {Shorten(Printable(Encoding.UTF8.GetString(frame)))}";
        return withDigest ? text + " · with the password's digest" : text;
    }

    /// <summary>"Status: relay 1 and 3 on", "Status: all off".</summary>
    public static string Status(ShellyStatus status, int relayCount)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Relays == 0 ? "Status: all off" : $"Status: {Channels(status.Relays, relayCount)} on";
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
