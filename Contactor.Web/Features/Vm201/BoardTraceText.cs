using System.Globalization;
using System.Text;
using Contactor.Vm201;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// A step on the board connection in plain Swedish for the Debug screen and its toasts, e.g. "Slå på relä 3" with
/// "02 06 4F 04 A5 03" below it.
/// </summary>
public static class BoardTraceText
{
    /// <returns>The text; the packet's bytes in hex, or null when there is no packet; and whether it is an error.</returns>
    public static (string Text, string? Hex, bool IsError) Describe(Vm201TraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind switch
        {
            Vm201TraceKind.Sent => (Sent(entry), Hex(entry), false),
            Vm201TraceKind.Received => (Received(entry), Hex(entry), entry.Command == Vm201Command.AccessDenied),
            Vm201TraceKind.Connecting => ($"Ansluter till {entry.Detail}", null, false),
            Vm201TraceKind.ConnectionLost when entry.Detail == Vm201Client.StoppedByApp => ("Appen stängde anslutningen", null, false),
            Vm201TraceKind.ConnectionLost => ($"Anslutningen bröts: {entry.Detail}", null, true),
            Vm201TraceKind.Reconnecting => ($"Försöker igen om {entry.Detail} s", null, false),
            Vm201TraceKind.DiscardedBytes => ($"Hoppade över {entry.Detail} trasiga byte från kortet", null, true),
            _ => (entry.Kind.ToString(), null, false),
        };
    }

    private static string Sent(Vm201TraceEntry entry)
    {
        var data = entry.Data;
        return entry.Command switch
        {
            Vm201Command.On when data.Length == 1 => $"Slå på {Relays(data[0])}",
            Vm201Command.Off when data.Length == 1 => $"Stäng av {Relays(data[0])}",
            Vm201Command.StatusRequest => "Fråga efter status",
            Vm201Command.TimerDisable when data.Length == 1 => $"Stäng av kortets egna timers för {Relays(data[0])}",
            Vm201Command.Username => "Användarnamn (dolt)",
            Vm201Command.Password => "Lösenord (dolt)",
            Vm201Command.Closed => "Avsluta anslutningen",
            _ => $"Kommando {Letter(entry.Command)}",
        };
    }

    private static string Received(Vm201TraceEntry entry)
    {
        var data = entry.Data;
        return entry.Command switch
        {
            Vm201Command.Auth => "Kortet vill ha inloggning",
            Vm201Command.LoggedIn => "Inloggad",
            Vm201Command.AccessDenied => "Fel användarnamn eller lösenord",
            Vm201Command.Closed => "Kortet stänger anslutningen",
            Vm201Command.Name when data.Length >= 1 => data[0] == 9 ? "Namnet på ingången" : $"Namnet på relä {data[0]}",
            Vm201Command.Status when data.Length == Vm201Command.StatusDataLength => Status(data),
            _ => $"Okänt svar {Letter(entry.Command)}",
        };
    }

    // "Status: relä 1 och 3 på · ingången på", plus the board's timers when any are on.
    private static string Status(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder("Status: ");
        text.Append(data[0] == 0 ? "alla av" : Relays(data[0]) + " på");
        if (data[1] != 0)
        {
            text.Append(" · kortets timers på för ").Append(Relays(data[1]));
        }

        text.Append((data[2] & 1) != 0 ? " · ingången på" : " · ingången av");
        return text.ToString();
    }

    /// <summary>"alla", "relä 3" or "relä 1, 3 och 5".</summary>
    public static string Relays(byte mask)
    {
        if (mask == Vm201Channel.All)
        {
            return "alla";
        }

        if (mask == 0)
        {
            return "inga relän";
        }

        List<string> channels = [];
        for (var channel = 1; channel <= Vm201Channel.Count; channel++)
        {
            if ((mask & Vm201Channel.Mask(channel)) != 0)
            {
                channels.Add(channel.ToString(CultureInfo.InvariantCulture));
            }
        }

        return channels.Count == 1
            ? $"relä {channels[0]}"
            : $"relä {string.Join(", ", channels[..^1])} och {channels[^1]}";
    }

    private static string Letter(byte command)
    {
        return command is >= 0x20 and < 0x7F ? ((char)command).ToString() : $"0x{command:X2}";
    }

    // A hidden user name or password packet holds only its header; the rest is shown as "…".
    private static string Hex(Vm201TraceEntry entry)
    {
        var frame = entry.Frame.Span;
        var text = new StringBuilder(frame.Length * 3 + 2);
        foreach (var b in frame)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        if (entry.Command is Vm201Command.Username or Vm201Command.Password)
        {
            text.Append(" …");
        }

        return text.ToString();
    }
}
