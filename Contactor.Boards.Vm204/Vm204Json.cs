using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Contactor.Boards.Vm204;

/// <summary>
/// The board's JSON answers, read strictly: anything that isn't what the firmware writes is refused with a
/// <see cref="Vm204ProtocolException"/>, never half-read. The answer is at most <see cref="Vm204Api.MaxResponseBytes"/>
/// long by then, and the depth is capped, so a broken board can't make it grow.
/// </summary>
public static class Vm204Json
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 4 };

    /// <summary>
    /// Reads a status answer, as <c>createJsonData</c> (<c>Main/main.c</c>) writes it:
    /// <c>{"relays": [b,b,b,b],"pulse": [b,b,b,b],"inputs": [b,b,b,b],"analog": n}</c>. <c>pulse</c> is ignored; an
    /// <c>analog</c> that is missing or not a whole number reads as null.
    /// </summary>
    public static Vm204Status ReadStatus(ReadOnlySpan<byte> json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new Vm204ProtocolException("The VM204's status isn't a JSON object.");
        }

        var relays = ReadSwitches(root, "relays", Vm204Api.RelayCount);
        var inputs = ReadSwitches(root, "inputs", Vm204Api.InputCount);
        int? analog = root.TryGetProperty("analog", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
        return new Vm204Status(relays, inputs, analog);
    }

    /// <summary>Reads a login answer, <c>{"ApiKey":"…"}</c> (<c>Main/api.c</c> createAPIKeyJson): letters and digits only.</summary>
    public static string ReadKey(ReadOnlySpan<byte> json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ApiKey", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new Vm204ProtocolException("The VM204's login answer has no key.");
        }

        var key = value.GetString() ?? "";
        if (key.Length is 0 or > Vm204Api.KeyMaxLength || !key.All(char.IsAsciiLetterOrDigit))
        {
            throw new Vm204ProtocolException("The VM204's key isn't letters and digits.");
        }

        return key;
    }

    /// <summary>A status as the firmware writes it, with its spaces; for the simulator.</summary>
    public static string WriteStatus(Vm204Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"relays\": [{Bits(status.Relays, Vm204Api.RelayCount)}],\"pulse\": [{Bits(0, Vm204Api.RelayCount)}],\"inputs\": [{Bits(status.Inputs, Vm204Api.InputCount)}],\"analog\": {status.Analog ?? 0}}}");
    }

    /// <summary>A login answer as the firmware writes it; for the simulator.</summary>
    public static string WriteKey(string key)
    {
        return $"{{\"ApiKey\":\"{key}\"}}";
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > Vm204Api.MaxResponseBytes)
        {
            throw new Vm204ProtocolException($"The VM204's answer is longer than {Vm204Api.MaxResponseBytes} bytes.");
        }

        try
        {
            return JsonDocument.Parse(json.ToArray(), Options);
        }
        catch (JsonException ex)
        {
            throw new Vm204ProtocolException("The VM204's answer isn't valid JSON.", ex);
        }
    }

    private static byte ReadSwitches(JsonElement root, string name, int count)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != count)
        {
            throw new Vm204ProtocolException($"The VM204's status has no \"{name}\" with {count} values.");
        }

        var bits = 0;
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.True)
            {
                bits |= 1 << index;
            }
            else if (item.ValueKind != JsonValueKind.False)
            {
                throw new Vm204ProtocolException($"The VM204's \"{name}\" holds something other than true or false.");
            }

            index++;
        }

        return (byte)bits;
    }

    private static string Bits(byte bits, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append((bits & (1 << i)) != 0 ? "true" : "false");
        }

        return text.ToString();
    }
}
