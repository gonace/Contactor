using System.Globalization;
using System.Text;

namespace Contactor.Boards.Vm208;

/// <summary>
/// The VM208's requests and answers in plain words, for the app's Debug screen and the simulator alike, e.g. "Turn on
/// relay 3" with "POST /relay index=3&amp;state=1&amp;interface=-1&amp;socket=0" below it. The board's API has no
/// login, key or password, so there is nothing secret to hide; a long answer is cut and anything unprintable shows
/// as "·".
/// </summary>
public static class Vm208TraceText
{
    // Enough for any answer the firmware sends with short names; a long one is cut.
    private const int MaxShownLength = 300;

    /// <summary>The request as the Debug screen shows its bytes: "GET /status?…" or "POST /relay index=…".</summary>
    public static string RequestBytes(string method, string pathAndQuery, string? form = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        return Shorten(form is null ? $"{method} {pathAndQuery}" : $"{method} {pathAndQuery} {form}");
    }

    /// <summary>What a request asks for: "Ask for status", "Turn on relay 3", "Turn off relay 2".</summary>
    public static string Request(string method, string path, string? form = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Equals(Vm208Api.StatusPath, StringComparison.OrdinalIgnoreCase))
        {
            return "Ask for status";
        }

        if (path.Equals(Vm208Api.RelayPath, StringComparison.OrdinalIgnoreCase) && form is not null && ReadRelayForm(form) is { } command)
        {
            return $"Turn {(command.TurnOn ? "on" : "off")} relay {command.Relay.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"Ask for {method} {Shorten(path)}";
    }

    /// <summary>"Status: relay 1 and 3 on · input on", with " · m1 on" when a transistor output is on.</summary>
    public static string Status(Vm208Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var text = new StringBuilder("Status: ");
        text.Append(status.Relays == 0 ? "all off" : Channels("relay", status.Relays, Vm208Api.RelayCount) + " on");
        text.Append(status.Input ? " · input on" : " · input off");
        text.Append(status.Outputs switch
        {
            1 => " · m1 on",
            2 => " · m2 on",
            3 => " · m1 and m2 on",
            _ => "",
        });
        return text.ToString();
    }

    /// <summary>An answer's bytes as text: "200 {…}", cut when long, with anything unprintable as "·".</summary>
    public static string Answer(int statusCode, string? body)
    {
        var code = statusCode.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(body))
        {
            return code;
        }

        return $"{code} {Shorten(body)}";
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

    /// <summary>
    /// The relay and state a <see cref="Vm208Api.RelayPath"/> form names, read as the firmware does: by position
    /// (index, state, interface, socket), <c>toInt</c> on the numbers, any state but 0 is on. Null unless it has the
    /// four parameters, the card itself as interface (-1) and a relay 1–4.
    /// </summary>
    public static (int Relay, bool TurnOn)? ReadRelayForm(string form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var values = form.Split('&').Select(pair =>
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            return Uri.UnescapeDataString((equals < 0 ? "" : pair[(equals + 1)..]).Replace('+', ' '));
        }).ToList();
        if (values.Count != 4 || ToInt(values[2]) != -1)
        {
            return null;
        }

        var relay = ToInt(values[0]);
        return relay is >= 1 and <= Vm208Api.RelayCount ? ((int)relay, ToInt(values[1]) != 0) : null;
    }

    // Arduino's String.toInt: the leading whole number, 0 when there is none.
    private static long ToInt(string value)
    {
        var text = value.TrimStart();
        var length = text.Length > 0 && text[0] is '-' or '+' ? 1 : 0;
        while (length < text.Length && length < 12 && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return long.TryParse(text.AsSpan(0, length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static string Shorten(string text)
    {
        var printable = new string([.. text.Select(c => c is >= ' ' and <= '~' ? c : '·')]);
        return printable.Length <= MaxShownLength ? printable : printable[..MaxShownLength] + "…";
    }
}
