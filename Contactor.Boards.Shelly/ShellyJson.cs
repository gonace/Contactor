using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Contactor.Boards.Shelly;

/// <summary>A response frame: its <c>result</c> or its <c>error</c>, which "are mutually exclusive" ("RPC Protocol").</summary>
/// <param name="Result">The result, a copy that outlives the parsed answer; null for an error frame.</param>
/// <param name="Error">The error; null for a result.</param>
public sealed record ShellyFrame(JsonElement? Result, ShellyRpcError? Error);

/// <summary>What <c>Shelly.GetStatus</c> says about the device's switches.</summary>
/// <param name="Count">How many <c>switch:N</c> components it has, numbered 0 to this minus one.</param>
/// <param name="Outputs">Bit per switch, bit 0 = <c>switch:0</c>; 1 = its <c>output</c> is true.</param>
/// <param name="ConfigRevision">
/// <c>sys.cfg_rev</c>, which grows with every change of the device's configuration ("Sys"); null when the status has
/// none, or one that isn't a whole number.
/// </param>
/// <param name="ScheduleRevision">
/// <c>sys.schedule_rev</c>, which grows with every change of the schedules ("Sys", "Schedule"); null when the status has
/// none, or one that isn't a whole number.
/// </param>
/// <param name="Inputs">
/// Bit per input, bit 0 = <c>input:0</c>: 1 for an input whose <c>state</c> is true or false, which the docs give only
/// for type <c>switch</c> ("Input", "Status"). A <c>button</c> (state null), a disabled input (state null), an
/// <c>analog</c> or <c>count</c> input (no state) and an input the app can't read are 0. Only <c>input:0</c> to
/// <c>input:3</c> (<see cref="ShellyApi.MaxInputCount"/>).
/// </param>
/// <param name="InputsOn">Bit per input, as <paramref name="Inputs"/>: 1 when its <c>state</c> is true.</param>
/// <param name="InputsPresent">
/// Bit per input, bit 0 = <c>input:0</c>: 1 for each <c>input:N</c> the status has, whatever its type or state. Only
/// <c>input:0</c> to <c>input:3</c>, so an add-on's inputs (<c>input:100</c> on) are left out.
/// </param>
public sealed record ShellySwitches(int Count, uint Outputs, long? ConfigRevision = null, long? ScheduleRevision = null, byte Inputs = 0, byte InputsOn = 0, byte InputsPresent = 0)
{
    /// <summary>
    /// How many inputs the device has for the app: every <c>input:N</c> it has, whatever its type, from <c>input:0</c>
    /// without a gap (Issue #623). An input after a gap isn't counted, so an input keeps its number (<c>input:N</c> is
    /// input N+1).
    /// </summary>
    public int InputCount => System.Numerics.BitOperations.TrailingZeroCount(~(uint)InputsPresent);
}

/// <summary>A request frame as the simulator reads it.</summary>
/// <param name="Id">The request's <c>id</c> as JSON text, to send back as it came: a number or a string.</param>
/// <param name="Source">The request's <c>src</c>, or null without one.</param>
/// <param name="Method">The method's name.</param>
/// <param name="Params">The parameters, a copy; null without any.</param>
public sealed record ShellyRequest(string Id, string? Source, string Method, JsonElement? Params);

/// <summary>
/// The JSON-RPC frames of the Gen2+ API ("RPC Protocol"), read strictly: anything that isn't what the docs describe is
/// refused with a <see cref="ShellyProtocolException"/>, never half-read. The answer is at most
/// <see cref="ShellyApi.MaxResponseBytes"/> long by then, and the depth is capped, so a broken device can't make it grow.
/// </summary>
public static class ShellyJson
{
    /// <summary>The most switch components a status may have; more isn't a Shelly the app knows.</summary>
    public const int MaxSwitches = 32;

    // A status nests about 5 deep (result, switch:0, aenergy, by_minute); room for components the app doesn't read.
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };

    private const int MaxIdLength = 64;

    /// <summary>
    /// A request frame: <c>{"id":1,"src":"contactor","method":"Switch.Set","params":{"id":0,"on":true}}</c> ("RPC
    /// Protocol", "Request Frame"). <paramref name="parameters"/> writes the members of <c>params</c>; null for none.
    /// </summary>
    public static byte[] WriteRequest(int id, string method, Action<Utf8JsonWriter>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("src", ShellyApi.Source);
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WriteStartObject("params");
                parameters(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary><c>Switch.Set</c> for one relay: <c>{"id":relay-1,"on":on}</c> ("Switch", "Switch.Set").</summary>
    public static byte[] WriteSwitchSet(int id, int relay, bool on)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, ShellyApi.MaxRelayCount);
        return WriteRequest(id, ShellyApi.SwitchSet, writer =>
        {
            writer.WriteNumber("id", relay - 1);
            writer.WriteBoolean("on", on);
        });
    }

    /// <summary><c>Switch.GetConfig</c> for one relay: <c>{"id":relay-1}</c> ("Switch", "Switch.GetConfig").</summary>
    public static byte[] WriteSwitchGetConfig(int id, int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, ShellyApi.MaxRelayCount);
        return WriteRequest(id, ShellyApi.SwitchGetConfig, writer => writer.WriteNumber("id", relay - 1));
    }

    /// <summary>
    /// <c>Switch.SetConfig</c> for one relay's state after a power cut, and nothing else of its configuration:
    /// <c>{"id":relay-1,"config":{"initial_state":"off"}}</c> ("Switch", "Switch.SetConfig").
    /// </summary>
    public static byte[] WriteSwitchSetPowerOn(int id, int relay, PowerOnState state)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, ShellyApi.MaxRelayCount);
        var initialState = ShellyApi.ToInitialState(state);
        return WriteRequest(id, ShellyApi.SwitchSetConfig, writer =>
        {
            writer.WriteNumber("id", relay - 1);
            writer.WriteStartObject("config");
            writer.WriteString(ShellyApi.InitialState, initialState);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Reads a switch's state after a power cut from <c>Switch.GetConfig</c>'s result ("Switch", "Configuration"): its
    /// <c>initial_state</c>, or null when it has none or one the docs don't list. Its <c>id</c>, when there, must be
    /// <paramref name="relay"/> minus 1: an answer about another switch is refused.
    /// </summary>
    public static PowerOnState? ReadPowerOn(JsonElement result, int relay)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ShellyProtocolException("The Shelly's switch settings aren't a JSON object.");
        }

        if (result.TryGetProperty("id", out var id) && (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != relay - 1))
        {
            throw new ShellyProtocolException("The Shelly's switch settings are for another switch.");
        }

        return ShellyApi.ToPowerOnState(String(result, ShellyApi.InitialState));
    }

    /// <summary>
    /// Reads a switch's auto-on from <c>Switch.GetConfig</c>'s result, after <see cref="ReadPowerOn"/> checked it ("Switch",
    /// "Configuration"): its <c>auto_on</c>, or null when it has none or one that isn't true or false.
    /// </summary>
    public static bool? ReadAutoOn(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty(ShellyApi.AutoOn, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }

    /// <summary>
    /// <c>Switch.SetConfig</c> that turns one relay's auto-on off, and nothing else of its configuration:
    /// <c>{"id":relay-1,"config":{"auto_on":false}}</c> ("Switch", "Switch.SetConfig").
    /// </summary>
    public static byte[] WriteSwitchAutoOnOff(int id, int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, ShellyApi.MaxRelayCount);
        return WriteRequest(id, ShellyApi.SwitchSetConfig, writer =>
        {
            writer.WriteNumber("id", relay - 1);
            writer.WriteStartObject("config");
            writer.WriteBoolean(ShellyApi.AutoOn, false);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// <c>Schedule.Update</c> that turns one job off and changes nothing else of it: <c>{"id":job,"enable":false}</c>, as
    /// the docs' own example ("Schedule", "Schedule.Update").
    /// </summary>
    public static byte[] WriteScheduleOff(int id, long job)
    {
        return WriteRequest(id, ShellyApi.ScheduleUpdate, writer =>
        {
            writer.WriteNumber("id", job);
            writer.WriteBoolean("enable", false);
        });
    }

    /// <summary>
    /// Reads <c>Schedule.List</c>'s result ("Schedule", "Schedule.List" and "Schedule Object Structure"): <c>jobs</c> an
    /// array of at most <see cref="ShellyApi.MaxScheduleJobs"/> objects, each with a whole-number <c>id</c> of its own,
    /// <c>enable</c> true or false (true when missing, as the docs say), and <c>calls</c> an array of at most
    /// <see cref="ShellyApi.MaxScheduleCalls"/>. Anything else is refused. In the calls, only those that switch one of
    /// the device's <paramref name="switchCount"/> switches count (<see cref="ShellyJob.Relays"/>); a call the app can't
    /// read, an id the device hasn't and any other method are skipped.
    /// </summary>
    public static ShellySchedules ReadSchedules(JsonElement result, int switchCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(switchCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(switchCount, ShellyApi.MaxRelayCount);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("jobs", out var jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            throw new ShellyProtocolException("The Shelly's schedules have no list of jobs.");
        }

        if (jobs.GetArrayLength() > ShellyApi.MaxScheduleJobs)
        {
            throw new ShellyProtocolException($"The Shelly lists more than {ShellyApi.MaxScheduleJobs} schedules.");
        }

        List<ShellyJob> read = [];
        foreach (var job in jobs.EnumerateArray())
        {
            if (job.ValueKind != JsonValueKind.Object
                || !job.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out var number))
            {
                throw new ShellyProtocolException("The Shelly has a schedule without an id.");
            }

            if (read.Exists(j => j.Id == number))
            {
                throw new ShellyProtocolException("The Shelly has two schedules with the same id.");
            }

            var enabled = true;
            if (job.TryGetProperty("enable", out var enable))
            {
                enabled = enable.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new ShellyProtocolException("The Shelly has a schedule that is neither on nor off."),
                };
            }

            if (!job.TryGetProperty("calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            {
                throw new ShellyProtocolException("The Shelly has a schedule without calls.");
            }

            if (calls.GetArrayLength() > ShellyApi.MaxScheduleCalls)
            {
                throw new ShellyProtocolException($"The Shelly has a schedule with more than {ShellyApi.MaxScheduleCalls} calls.");
            }

            var relays = 0;
            foreach (var call in calls.EnumerateArray())
            {
                relays |= CallRelays(call, switchCount);
            }

            read.Add(new ShellyJob(number, enabled, (byte)relays));
        }

        long? revision = result.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.Number && rev.TryGetInt64(out var r) ? r : null;
        return new ShellySchedules(read, revision);
    }

    // The relays one call in a job switches, bit 0 = switch:0; 0 for a call the app can't read or that switches none.
    private static int CallRelays(JsonElement call, int switchCount)
    {
        if (call.ValueKind != JsonValueKind.Object || String(call, "method") is not { } method)
        {
            return 0;
        }

        var parameters = call.TryGetProperty("params", out var value) && value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
        switch (method)
        {
            case ShellyApi.SwitchSet or ShellyApi.SwitchToggle:
                return parameters is { } p && p.TryGetProperty("id", out var id) ? SwitchBit(id, switchCount) : 0;
            case ShellyApi.SwitchSetAll:
                return (1 << switchCount) - 1;
            case ShellyApi.SwitchSetMany:
                if (parameters is not { } many || !many.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                {
                    return 0;
                }

                var mask = 0;
                foreach (var each in ids.EnumerateArray())
                {
                    mask |= SwitchBit(each, switchCount);
                }

                return mask;
            default:
                return 0;
        }
    }

    // A switch id as its bit, or 0 for one that isn't a whole number the device has.
    private static int SwitchBit(JsonElement id, int switchCount)
    {
        return id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number >= 0 && number < switchCount ? 1 << number : 0;
    }

    /// <summary>
    /// Reads a response frame and checks it answers request <paramref name="expectedId"/>: an object with a number
    /// <c>id</c>, and either a <c>result</c> object or an <c>error</c> with a number <c>code</c> and a string
    /// <c>message</c>.
    /// </summary>
    public static ShellyFrame ReadFrame(ReadOnlySpan<byte> json, int expectedId)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ShellyProtocolException("The Shelly's answer isn't a JSON object.");
        }

        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != expectedId)
        {
            throw new ShellyProtocolException("The Shelly's answer is for another request.");
        }

        var hasResult = root.TryGetProperty("result", out var result);
        var hasError = root.TryGetProperty("error", out var error);
        if (hasResult == hasError)
        {
            throw new ShellyProtocolException("The Shelly's answer has neither a result nor an error, or both.");
        }

        if (hasError)
        {
            return new ShellyFrame(null, ReadError(error));
        }

        // Switch.SetAll answers null ("Switch", its example); the methods the app calls answer an object.
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ShellyProtocolException("The Shelly's result isn't a JSON object.");
        }

        return new ShellyFrame(result.Clone(), null);
    }

    /// <summary>
    /// Reads an error frame, or null when <paramref name="json"/> isn't one: for an answer with another status than 200,
    /// which may still carry the device's error.
    /// </summary>
    public static ShellyRpcError? TryReadError(ReadOnlySpan<byte> json, int expectedId)
    {
        try
        {
            return ReadFrame(json, expectedId).Error;
        }
        catch (ShellyProtocolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>Shelly.GetDeviceInfo</c>'s result ("Shelly", "Shelly.GetDeviceInfo"): <c>id</c> is required; <c>gen</c>,
    /// <c>ver</c>, <c>model</c> and <c>profile</c> are read when they are there, and null otherwise.
    /// </summary>
    public static ShellyDeviceInfo ReadDeviceInfo(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ShellyProtocolException("The Shelly's device info isn't a JSON object.");
        }

        var id = String(result, "id");
        if (id is null || !IsPlainId(id))
        {
            throw new ShellyProtocolException("The Shelly's device info has no device id.");
        }

        int? gen = result.TryGetProperty("gen", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
        return new ShellyDeviceInfo(id, String(result, "model"), gen, String(result, "ver"), String(result, "profile"));
    }

    /// <summary>
    /// Reads the switches in <c>Shelly.GetStatus</c>'s result ("Shelly", "Shelly.GetStatus"; "Switch", "Status"): every
    /// member <c>switch:N</c> must be an object with a boolean <c>output</c>, and their ids 0 to the count minus one.
    /// The inputs (<c>input:N</c>) are read too, but never refuse the status (<see cref="ShellySwitches.Inputs"/>).
    /// Every other component is skipped.
    /// </summary>
    public static ShellySwitches ReadSwitches(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ShellyProtocolException("The Shelly's status isn't a JSON object.");
        }

        var seen = 0UL;
        var outputs = 0U;
        var count = 0;
        foreach (var component in result.EnumerateObject())
        {
            if (!component.Name.StartsWith(ShellyApi.SwitchKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = component.Name[ShellyApi.SwitchKeyPrefix.Length..];
            var index = suffix.Length is > 0 and <= 2 && suffix.All(char.IsAsciiDigit) && (suffix.Length == 1 || suffix[0] != '0')
                ? int.Parse(suffix, CultureInfo.InvariantCulture)
                : -1;
            if (index is < 0 or >= MaxSwitches || (seen & (1UL << index)) != 0)
            {
                throw new ShellyProtocolException("The Shelly's status has a switch the app can't read.");
            }

            if (component.Value.ValueKind != JsonValueKind.Object
                || !component.Value.TryGetProperty("output", out var output)
                || output.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new ShellyProtocolException($"The Shelly's \"{component.Name}\" has no output that is true or false.");
            }

            seen |= 1UL << index;
            count++;
            if (output.ValueKind == JsonValueKind.True)
            {
                outputs |= 1U << index;
            }
        }

        // Numbered from 0 without a gap: switch:0 is relay 1.
        if (seen != (1UL << count) - 1)
        {
            throw new ShellyProtocolException("The Shelly's switches aren't numbered from 0 without a gap.");
        }

        var (inputs, inputsOn, present) = ReadInputs(result);
        return new ShellySwitches(count, outputs, SysRevision(result, ShellyApi.ConfigRevision), SysRevision(result, ShellyApi.ScheduleRevision), inputs, inputsOn, present);
    }

    // The inputs in a status: every input:0 to input:3 it has, and those with a state that is true or false ("Input",
    // "Status": "state ... (only for type switch, button) ... null if the input instance is stateless, i.e. for type
    // button", and null for an input that isn't enabled). Never throws: an input whose state the app can't read (an id
    // it can't read, a state of another kind, the same input twice) is there but not read, so the inputs can never fail
    // the poll.
    private static (byte Inputs, byte On, byte Present) ReadInputs(JsonElement result)
    {
        var seen = 0;
        var twice = 0;
        var inputs = 0;
        var on = 0;
        foreach (var component in result.EnumerateObject())
        {
            if (!component.Name.StartsWith(ShellyApi.InputKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // input:0 to input:3 only, without a leading zero or sign.
            var suffix = component.Name.AsSpan(ShellyApi.InputKeyPrefix.Length);
            if (suffix.Length != 1 || suffix[0] < '0' || suffix[0] >= '0' + ShellyApi.MaxInputCount)
            {
                continue;
            }

            var index = suffix[0] - '0';
            var bit = 1 << index;
            twice |= seen & bit;
            seen |= bit;
            if (component.Value.ValueKind == JsonValueKind.Object
                && component.Value.TryGetProperty("state", out var state)
                && state.ValueKind is (JsonValueKind.True or JsonValueKind.False)
                && (!component.Value.TryGetProperty("id", out var id) || (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == index)))
            {
                inputs |= bit;
                on |= state.ValueKind == JsonValueKind.True ? bit : 0;
            }
        }

        // An input given twice is ambiguous: there, but not read.
        return ((byte)(inputs & ~twice), (byte)(on & ~twice), (byte)seen);
    }

    /// <summary>
    /// Reads a request frame, for the simulator: an object with an <c>id</c> (a number or a string) and a string
    /// <c>method</c>, and <c>params</c> an object when there are any. Null for anything else.
    /// </summary>
    public static ShellyRequest? TryReadRequest(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
                || id.GetRawText().Length > MaxIdLength
                || String(root, "method") is not { } method)
            {
                return null;
            }

            JsonElement? parameters = null;
            if (root.TryGetProperty("params", out var value))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                parameters = value.Clone();
            }

            return new ShellyRequest(id.GetRawText(), String(root, "src"), method, parameters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A response frame with a result, as the device writes it ("RPC Protocol", "Response Frame"); for the simulator.</summary>
    public static string WriteResult(string id, string source, string? destination, Action<Utf8JsonWriter> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return WriteFrame(id, source, destination, writer =>
        {
            writer.WriteStartObject("result");
            result(writer);
            writer.WriteEndObject();
        });
    }

    /// <summary>A response frame with an error, as "Common Errors" shows them; for the simulator.</summary>
    public static string WriteError(string id, string source, string? destination, ShellyRpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return WriteFrame(id, source, destination, writer =>
        {
            writer.WriteStartObject("error");
            writer.WriteNumber("code", error.Code);
            writer.WriteString("message", error.Message);
            writer.WriteEndObject();
        });
    }

    // sys.cfg_rev or sys.schedule_rev, or null without a whole number there: then the app reads again every minute.
    private static long? SysRevision(JsonElement result, string name)
    {
        return result.TryGetProperty("sys", out var sys)
            && sys.ValueKind == JsonValueKind.Object
            && sys.TryGetProperty(name, out var revision)
            && revision.ValueKind == JsonValueKind.Number
            && revision.TryGetInt64(out var number)
                ? number
                : null;
    }

    // A device id is also the digest realm, which goes in quotes in the Authorization header.
    private static bool IsPlainId(string id)
    {
        return id.Length is > 0 and <= MaxIdLength && id.All(c => c is > ' ' and <= '~' and not '"' and not '\\');
    }

    private static ShellyRpcError ReadError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.Number
            || !code.TryGetInt32(out var number))
        {
            throw new ShellyProtocolException("The Shelly's error has no code.");
        }

        return new ShellyRpcError(number, String(error, "message") ?? "");
    }

    private static string? String(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string WriteFrame(string id, string source, string? destination, Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new ArrayBufferWriter<byte>();

        // As the device writes text: "Missing required 'id'", not with the quote as \u0027.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteRawValue(id);
            writer.WriteString("src", source);
            if (destination is not null)
            {
                writer.WriteString("dst", destination);
            }

            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > ShellyApi.MaxResponseBytes)
        {
            throw new ShellyProtocolException($"The Shelly's answer is longer than {ShellyApi.MaxResponseBytes} bytes.");
        }

        try
        {
            return JsonDocument.Parse(json.ToArray(), Options);
        }
        catch (JsonException ex)
        {
            throw new ShellyProtocolException("The Shelly's answer isn't valid JSON.", ex);
        }
    }
}
