using System.Globalization;

namespace Contactor.Boards.Shelly;

/// <summary>
/// An error frame's <c>error</c>: "code: number, code identifying the type of the error" and "message: string,
/// description of the error" ("Common Errors"). <see cref="ToString"/> shows the device's own message, made safe to
/// show: printable characters only, at most 200 of them.
/// </summary>
public sealed record ShellyRpcError(int Code, string Message)
{
    private const int MaxShownLength = 200;

    /// <summary>The device's message as the screens may show it.</summary>
    public string ShownMessage => Printable(Message);

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{ShownMessage} (error {Code})");
    }

    /// <summary>Text from the device with anything unprintable as "·" and cut at 200 characters.</summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var printable = new string([.. text.Take(MaxShownLength).Select(c => char.IsControl(c) || char.IsSurrogate(c) ? '·' : c)]);
        return text.Length > MaxShownLength ? printable + "…" : printable;
    }
}
