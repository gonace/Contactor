using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Contactor.Boards.Vm208;

/// <summary>
/// The board's JSON answer, read strictly where the app uses it: anything that isn't what the firmware writes is
/// refused with a <see cref="Vm208ProtocolException"/>, never half-read. The answer is at most
/// <see cref="Vm208Api.MaxResponseBytes"/> long by then, and the depth is capped, so a broken board can't make it grow.
/// </summary>
public static class Vm208Json
{
    /// <summary>The names the board has until someone renames its relays (<c>data/names.json</c>).</summary>
    public static IReadOnlyList<string> FactoryNames { get; } = ["RELAY1", "RELAY2", "RELAY3", "RELAY4"];

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 4 };

    // ArduinoJson 5 escapes only quotes, backslashes and control characters, and writes everything else as it is.
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Reads a status answer, as <c>sendIOState(request, interface, socket)</c> (<c>src/Network/server_VM208.cpp</c>)
    /// writes it for the card itself:
    /// <c>{"Channels":[{"name":"…","state":b,"pulseActive":b,"timerActive":b}, …4],"m1":b,"m2":b,"input":b}</c>.
    /// Each relay's <c>state</c> and the <c>input</c> must be true or false; the names, the pulse and timer flags are
    /// ignored, and an <c>m1</c> or <c>m2</c> that isn't <c>true</c> reads as off.
    /// </summary>
    public static Vm208Status ReadStatus(ReadOnlySpan<byte> json)
    {
        if (json.Length > Vm208Api.MaxResponseBytes)
        {
            throw new Vm208ProtocolException($"The VM208's answer is longer than {Vm208Api.MaxResponseBytes} bytes.");
        }

        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new Vm208ProtocolException("The VM208's status isn't a JSON object.");
        }

        if (!root.TryGetProperty("Channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
        {
            throw new Vm208ProtocolException("The VM208's status has no \"Channels\".");
        }

        // The firmware sends no channels (and no input) when it couldn't take its I/O lock within a second.
        if (channels.GetArrayLength() == 0)
        {
            throw new Vm208ProtocolException("The VM208 was busy and sent no relays.") { IsBusy = true };
        }

        if (channels.GetArrayLength() != Vm208Api.RelayCount)
        {
            throw new Vm208ProtocolException($"The VM208's status doesn't have {Vm208Api.RelayCount} relays.");
        }

        var relays = 0;
        var index = 0;
        foreach (var channel in channels.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.Object || !channel.TryGetProperty("state", out var state) || !IsBoolean(state))
            {
                throw new Vm208ProtocolException("A relay in the VM208's status has no \"state\" of true or false.");
            }

            if (state.ValueKind == JsonValueKind.True)
            {
                relays |= 1 << index;
            }

            index++;
        }

        if (!root.TryGetProperty("input", out var input) || !IsBoolean(input))
        {
            throw new Vm208ProtocolException("The VM208's status has no \"input\" of true or false.");
        }

        var outputs = (IsTrue(root, "m1") ? 1 : 0) | (IsTrue(root, "m2") ? 2 : 0);
        return new Vm208Status((byte)relays, input.ValueKind == JsonValueKind.True, (byte)outputs);
    }

    /// <summary>A status as the firmware writes it, compact, with these relay names; for the simulator.</summary>
    public static string WriteStatus(Vm208Status status, IReadOnlyList<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        names ??= FactoryNames;
        if (names.Count != Vm208Api.RelayCount)
        {
            throw new ArgumentException($"Give {Vm208Api.RelayCount} names.", nameof(names));
        }

        var text = new StringBuilder("{\"Channels\":[");
        for (var i = 0; i < Vm208Api.RelayCount; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append("{\"name\":").Append(JsonSerializer.Serialize(names[i], WriteOptions))
                .Append(",\"state\":").Append(Bool((status.Relays & (1 << i)) != 0))
                .Append(",\"pulseActive\":false,\"timerActive\":false}");
        }

        text.Append("],\"m1\":").Append(Bool((status.Outputs & 1) != 0))
            .Append(",\"m2\":").Append(Bool((status.Outputs & 2) != 0))
            .Append(",\"input\":").Append(Bool(status.Input))
            .Append('}');
        return text.ToString();
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            // Decoded first, with anything that isn't UTF-8 replaced: a name with broken bytes, typed on the board's
            // own page, must not make every status unreadable.
            return JsonDocument.Parse(Encoding.UTF8.GetString(json), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new Vm208ProtocolException("The VM208's answer isn't valid JSON.", ex);
        }
    }

    private static bool IsBoolean(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsTrue(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }
}
