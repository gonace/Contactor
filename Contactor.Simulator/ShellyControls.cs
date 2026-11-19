using System.Globalization;
using System.Text.Json;
using Contactor.Boards;
using Contactor.Boards.Shelly;

namespace Contactor.Simulator;

/// <summary>
/// The page's controls for a simulated Shelly's special cases: asking the app to wait (429), its firmware and profile,
/// a refused <c>Switch.Set</c>, each switch's <c>auto_on</c> and <c>initial_state</c>, and its schedule jobs. Each
/// changes the device at once, as on its own web page, and says so in the traffic. What the page never offers changes
/// nothing.
/// </summary>
public sealed class ShellyControls
{
    /// <summary>The longest firmware version the page takes.</summary>
    public const int MaxFirmwareLength = 32;

    private readonly ShellySimulator _device;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();

    public ShellyControls(ShellySimulator device, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);
        _device = device;
        _log = log;
    }

    /// <summary>The profiles the page offers: none (a device with one profile), "switch" and "cover".</summary>
    public static IReadOnlyList<string> Profiles { get; } = ["", "switch", "cover"];

    /// <summary>The <c>initial_state</c> values the docs list.</summary>
    public static IReadOnlyList<string> InitialStates { get; } = [.. ShellyApi.PowerOn.States.Select(ShellyApi.ToInitialState)];

    public int RelayCount => _device.Model.Capabilities.RelayCount;

    /// <summary>
    /// Whether the device has a password. Only then does it ask for the digest, so only then can it ask the app to wait
    /// (429), and only then does the firmware choose the digest's way.
    /// </summary>
    public bool HasPassword => _device.HasLogin;

    /// <summary>Whether every request that needs the password gets 429.</summary>
    public bool Throttle => _device.Throttle;

    public string Firmware => _device.Firmware;

    /// <summary>The profile, or "" for none.</summary>
    public string Profile => _device.Profile ?? "";

    /// <summary>The relays whose <c>Switch.Set</c> the device refuses.</summary>
    public ChannelMask Refused => new(_device.FailRelays);

    /// <summary>The relays with <c>auto_on</c>.</summary>
    public ChannelMask AutoOn => ChannelMask.Of(Enumerable.Range(1, RelayCount).Where(_device.AutoOn));

    public IReadOnlyList<ShellySimulatedSchedule> Schedules => _device.Schedules;

    public string InitialState(int relay)
    {
        return _device.InitialState(relay);
    }

    /// <summary>Makes every request that needs the password get 429; nothing for a device without a password.</summary>
    public void SetThrottle(bool throttle)
    {
        if (!HasPassword)
        {
            return;
        }

        lock (_lock)
        {
            if (_device.Throttle == throttle)
            {
                return;
            }

            _device.Throttle = throttle;
        }

        _log(throttle ? "Asking the app to wait (429) turned on here" : "Asking the app to wait (429) turned off here");
    }

    /// <summary>
    /// Sets the firmware version: 1 to <see cref="MaxFirmwareLength"/> letters, digits, '.', '-', '_' or '+', also one
    /// the app can't read. Returns null when it is set, otherwise why not, in words for the page.
    /// </summary>
    public string? SetFirmware(string? firmware)
    {
        var text = firmware?.Trim() ?? "";
        if (text.Length is 0 or > MaxFirmwareLength || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
        {
            return string.Create(CultureInfo.InvariantCulture, $"The firmware is 1 to {MaxFirmwareLength} letters, digits, '.', '-', '_' or '+'.");
        }

        lock (_lock)
        {
            if (_device.Firmware == text)
            {
                return null;
            }

            _device.Firmware = text;
        }

        _log($"Firmware set to {text} here");
        return null;
    }

    /// <summary>Sets the profile: one of <see cref="Profiles"/>, "" for none.</summary>
    public void SetProfile(string? profile)
    {
        if (profile is null || !Profiles.Contains(profile))
        {
            return;
        }

        lock (_lock)
        {
            if ((_device.Profile ?? "") == profile)
            {
                return;
            }

            _device.Profile = profile.Length == 0 ? null : profile;
        }

        _log(profile.Length == 0 ? "Profile set to none here" : $"Profile set to {profile} here");
    }

    /// <summary>Makes the device refuse, or carry out again, <c>Switch.Set</c> for one relay; the others stay as they are.</summary>
    public void SetRefused(int relay, bool refuse)
    {
        if (relay < 1 || relay > RelayCount)
        {
            return;
        }

        var bit = (byte)(1 << (relay - 1));
        lock (_lock)
        {
            var before = _device.FailRelays;
            var after = refuse ? (byte)(before | bit) : (byte)(before & ~bit);
            if (after == before)
            {
                return;
            }

            _device.FailRelays = after;
        }

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"Switch.Set for relay {relay} {(refuse ? "refused from now on" : "carried out again")} here"));
    }

    public void SetAutoOn(int relay, bool autoOn)
    {
        if (relay < 1 || relay > RelayCount)
        {
            return;
        }

        lock (_lock)
        {
            if (_device.AutoOn(relay) == autoOn)
            {
                return;
            }

            _device.SetAutoOn(relay, autoOn);
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"Auto-on for relay {relay} turned {(autoOn ? "on" : "off")} here"));
    }

    /// <summary>Sets a relay's state after a power cut: one of <see cref="InitialStates"/>.</summary>
    public void SetInitialState(int relay, string? initialState)
    {
        if (relay < 1 || relay > RelayCount || initialState is null || !InitialStates.Contains(initialState))
        {
            return;
        }

        lock (_lock)
        {
            if (_device.InitialState(relay) == initialState)
            {
                return;
            }

            _device.SetInitialState(relay, initialState);
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"Relay {relay} set to {initialState} after a power cut here"));
    }

    /// <summary>
    /// Adds a job that switches one relay on or off every day at <paramref name="time"/> ("HH:mm"), as the device's own
    /// web page would. At most <see cref="ShellyApi.MaxScheduleJobs"/>, as the device holds. Returns null when it is
    /// added, otherwise why not, in words for the page.
    /// </summary>
    public string? AddSchedule(int relay, bool turnOn, string? time)
    {
        if (relay < 1 || relay > RelayCount)
        {
            return "Choose a relay.";
        }

        if (time is null || time.Length > 5 || !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return "The time is HH:mm, e.g. 08:00.";
        }

        long id;
        lock (_lock)
        {
            if (_device.Schedules.Count >= ShellyApi.MaxScheduleJobs)
            {
                return string.Create(CultureInfo.InvariantCulture, $"The device holds at most {ShellyApi.MaxScheduleJobs} schedules.");
            }

            var calls = string.Create(
                CultureInfo.InvariantCulture,
                $$$"""[{"method":"Switch.Set","params":{"id":{{{relay - 1}}},"on":{{{(turnOn ? "true" : "false")}}}}}]""");
            id = _device.AddSchedule(calls, enable: true, string.Create(CultureInfo.InvariantCulture, $"0 {at.Minute} {at.Hour} * * *"));
        }

        var added = _device.Schedules.First(s => s.Id == id);
        _log(string.Create(CultureInfo.InvariantCulture, $"Schedule {id} added here: {Describe(added)}"));
        return null;
    }

    /// <summary>Turns a job on or off; nothing for a job the device doesn't hold, or one already so.</summary>
    public void SetScheduleEnabled(long id, bool enable)
    {
        if (_device.TrySetScheduleEnabled(id, enable))
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"Schedule {id} turned {(enable ? "on" : "off")} here"));
        }
    }

    /// <summary>
    /// A job in words, e.g. "08:00 every day · relay 1 on": a time of the page's own form, otherwise the timespec as it
    /// is; each <c>Switch.Set</c> call as its relay, and any other call by its method.
    /// </summary>
    public static string Describe(ShellySimulatedSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return $"{DescribeTime(schedule.Timespec)} · {DescribeCalls(schedule.Calls)}";
    }

    private static string DescribeTime(string timespec)
    {
        var parts = timespec.Split(' ');
        return parts is ["0", var minute, var hour, "*", "*", "*"]
            && int.TryParse(minute, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 0 and < 60
            && int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 0 and < 24
            ? string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00} every day")
            : timespec;
    }

    private static string DescribeCalls(string calls)
    {
        try
        {
            using var document = JsonDocument.Parse(calls);
            var words = document.RootElement.EnumerateArray().Select(DescribeCall).ToList();
            return words.Count == 0 ? "no calls" : string.Join(", ", words);
        }
        catch (JsonException)
        {
            return "calls the page can't read";
        }
    }

    private static string DescribeCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
        {
            return "a call";
        }

        if (method.GetString() == "Switch.Set"
            && call.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out var switchId)
            && switchId is >= 0 and < 64
            && parameters.TryGetProperty("on", out var on)
            && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return string.Create(CultureInfo.InvariantCulture, $"relay {switchId + 1} {(on.GetBoolean() ? "on" : "off")}");
        }

        // A method of any length a test gave: only its start, so the page stays readable.
        var name = method.GetString() ?? "";
        return name.Length == 0 ? "a call" : name.Length > 40 ? $"{name[..40]}…" : name;
    }
}
