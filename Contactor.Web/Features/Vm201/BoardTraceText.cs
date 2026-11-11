using System.Globalization;
using System.Text;
using Contactor.Boards.Vm201;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// A step on the board connection in plain words for the Debug screen and its toasts, e.g. "Turn on relay 3" with
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
            Vm201TraceKind.Connecting => ($"Connecting to {entry.Detail}", null, false),
            Vm201TraceKind.ConnectionLost when entry.Detail == Vm201Client.StoppedByApp => ("The app closed the connection", null, false),
            Vm201TraceKind.ConnectionLost => ($"Connection lost: {entry.Detail}", null, true),
            Vm201TraceKind.Reconnecting => ($"Trying again in {entry.Detail} s", null, false),
            Vm201TraceKind.DiscardedBytes => ($"Skipped {entry.Detail} broken bytes from the board", null, true),
            _ => (entry.Kind.ToString(), null, false),
        };
    }

    private static string Sent(Vm201TraceEntry entry)
    {
        var data = entry.Data;
        return entry.Command switch
        {
            Vm201Command.On when data.Length == 1 => $"Turn on {Relays(data[0])}",
            Vm201Command.Off when data.Length == 1 => $"Turn off {Relays(data[0])}",
            Vm201Command.StatusRequest => "Ask for status",
            Vm201Command.TimerDisable when data.Length == 1 => $"Turn off the board's own timers for {Relays(data[0])}",
            Vm201Command.Username => "User name (hidden)",
            Vm201Command.Password => "Password (hidden)",
            Vm201Command.Closed => "Avsluta anslutningen",
            _ => $"Kommando {Letter(entry.Command)}",
        };
    }

    private static string Received(Vm201TraceEntry entry)
    {
        var data = entry.Data;
        return entry.Command switch
        {
            Vm201Command.Auth => "The board asks for a login",
            Vm201Command.LoggedIn => "Logged in",
            Vm201Command.AccessDenied => "Wrong user name or password",
            Vm201Command.Closed => "The board closes the connection",
            Vm201Command.Name when data.Length >= 1 => data[0] == 9 ? "The input's name" : $"The name of relay {data[0]}",
            Vm201Command.Status when data.Length == Vm201Command.StatusDataLength => Status(data),
            _ => $"Unknown answer {Letter(entry.Command)}",
        };
    }

    // "Status: relay 1 and 3 on · input on", plus the board's timers when any are on.
    private static string Status(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder("Status: ");
        text.Append(data[0] == 0 ? "all off" : Relays(data[0]) + " on");
        if (data[1] != 0)
        {
            text.Append(" · the board's timers on for ").Append(Relays(data[1]));
        }

        text.Append((data[2] & 1) != 0 ? " · input on" : " · input off");
        return text.ToString();
    }

    /// <summary>"all", "relay 3" or "relay 1, 3 and 5".</summary>
    public static string Relays(byte mask)
    {
        if (mask == Vm201Channel.All)
        {
            return "all";
        }

        if (mask == 0)
        {
            return "no relays";
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
            ? $"relay {channels[0]}"
            : $"relay {string.Join(", ", channels[..^1])} and {channels[^1]}";
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
