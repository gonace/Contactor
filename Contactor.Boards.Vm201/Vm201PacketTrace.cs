using System.Globalization;
using System.Text;

namespace Contactor.Boards.Vm201;

/// <summary>
/// A packet as one log line: its command letter and its bytes in hex, e.g. "O 02 06 4F 01 A8 03". A user name or
/// password packet shows only its command and length: the credentials never reach a log.
/// </summary>
public static class Vm201PacketTrace
{
    public static string Describe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
        {
            return $"? {Hex(frame)}";
        }

        var command = frame[2];
        var letter = command is >= 0x20 and < 0x7F ? ((char)command).ToString() : "?";
        if (command is Vm201Command.Username or Vm201Command.Password)
        {
            return $"{letter} ({frame.Length} bytes, hidden)";
        }

        return $"{letter} {Hex(frame)}";
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
