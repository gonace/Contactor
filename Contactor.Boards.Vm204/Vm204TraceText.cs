using System.Globalization;
using System.Text;

namespace Contactor.Boards.Vm204;

/// <summary>
/// The VM204's requests and answers in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn on
/// relay 3" with "GET /api/relay/on?key=…&amp;relay=3" below it. The key, the user name and the password never show.
/// </summary>
public static class Vm204TraceText
{
    /// <summary>The query parameters that are never shown: the key works like a password.</summary>
    private static readonly string[] Hidden = ["key", "user", "password"];

    // Enough for any answer the firmware sends; a broken board's long answer is cut.
    private const int MaxShownLength = 200;

    /// <summary>A request target with the key, user name and password replaced by "…".</summary>
    public static string Redact(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return Shorten(pathAndQuery);
        }

        var parameters = pathAndQuery[(question + 1)..].Split('&').Select(pair =>
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            return Hidden.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"{name}=…" : pair;
        });
        return Shorten($"{pathAndQuery[..question]}?{string.Join('&', parameters)}");
    }

    /// <summary>What a request asks for: "Log in", "Ask for status", "Turn on relay 1 and 3".</summary>
    public static string Request(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? pathAndQuery : pathAndQuery[..question];
        var query = question < 0 ? "" : pathAndQuery[(question + 1)..];
        return path switch
        {
            Vm204Api.AuthPath => "Log in",
            Vm204Api.StatusPath => "Ask for status",
            Vm204Api.RelayOnPath => $"Turn on {RelayList(query)}",
            Vm204Api.RelayOffPath => $"Turn off {RelayList(query)}",
            _ => $"Ask for {Shorten(path)}",
        };
    }

    /// <summary>"Status: relay 1 and 3 on · input 2 on · analog 512".</summary>
    public static string Status(Vm204Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var text = new StringBuilder("Status: ");
        text.Append(status.Relays == 0 ? "all off" : Channels("relay", status.Relays, Vm204Api.RelayCount) + " on");
        text.Append(status.Inputs == 0 ? " · inputs off" : $" · {Channels("input", status.Inputs, Vm204Api.InputCount)} on");
        if (status.Analog is { } analog)
        {
            text.Append(" · analog ").Append(analog.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>An answer's bytes as text: "200 {…}", with a key never shown and anything unprintable as "·".</summary>
    public static string Answer(int statusCode, string? body, bool hideBody = false)
    {
        var code = statusCode.ToString(CultureInfo.InvariantCulture);
        if (hideBody)
        {
            return $"{code} {{\"ApiKey\":\"…\"}}";
        }

        if (string.IsNullOrEmpty(body))
        {
            return code;
        }

        var printable = new string([.. body.Select(c => c is >= ' ' and <= '~' ? c : '·')]);
        return $"{code} {Shorten(printable)}";
    }

    /// <summary>"all", "relay 3" or "relay 1, 3 and 4".</summary>
    public static string Channels(string noun, byte mask, int count)
    {
        if (mask == (1 << count) - 1)
        {
            return noun == "relay" ? "all" : $"all {noun}s";
        }

        if (mask == 0)
        {
            return $"no {noun}s";
        }

        List<string> channels = [];
        for (var channel = 1; channel <= count; channel++)
        {
            if ((mask & (1 << (channel - 1))) != 0)
            {
                channels.Add(channel.ToString(CultureInfo.InvariantCulture));
            }
        }

        return channels.Count == 1
            ? $"{noun} {channels[0]}"
            : $"{noun} {string.Join(", ", channels[..^1])} and {channels[^1]}";
    }

    /// <summary>The relay mask a <c>relay=1,3</c> parameter names, as the firmware reads it: numbers 1–4, others ignored.</summary>
    public static byte RelayMask(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var mask = 0;
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || !pair[..equals].Equals("relay", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in pair[(equals + 1)..].Split(','))
            {
                if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var relay) && relay is >= 1 and <= Vm204Api.RelayCount)
                {
                    mask |= 1 << (relay - 1);
                }
            }
        }

        return (byte)mask;
    }

    /// <summary>The <c>relay=</c> value for a mask: "1,3".</summary>
    public static string RelayParameter(byte mask)
    {
        return string.Join(',', Enumerable.Range(1, Vm204Api.RelayCount)
            .Where(relay => (mask & (1 << (relay - 1))) != 0)
            .Select(relay => relay.ToString(CultureInfo.InvariantCulture)));
    }

    private static string RelayList(string query)
    {
        return Channels("relay", RelayMask(query), Vm204Api.RelayCount);
    }

    private static string Shorten(string text)
    {
        return text.Length <= MaxShownLength ? text : text[..MaxShownLength] + "…";
    }
}
