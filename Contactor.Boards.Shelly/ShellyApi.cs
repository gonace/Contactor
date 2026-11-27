namespace Contactor.Boards.Shelly;

/// <summary>
/// What the app uses of a Shelly Gen2+ device's RPC API over HTTP, from Shelly's Gen2+ API docs
/// (https://shelly-api-docs.shelly.cloud/gen2/, read 2026-11-18). The device never sends anything by itself over HTTP
/// ("RPC Channels": no keepalive, no notifications), so the app polls it.
/// </summary>
public static class ShellyApi
{
    /// <summary>
    /// The one route the app uses: <c>POST /rpc</c> with a whole request frame as the body, answered with a whole
    /// response frame ("RPC Channels", "POST request with entire frame as payload").
    /// </summary>
    public const string RpcPath = "/rpc";

    /// <summary>The device's HTTP port: the docs' examples all use plain <c>http://${SHELLY}/rpc</c>.</summary>
    public const int DefaultPort = 80;

    /// <summary>The digest user name: "must be set to admin" ("Authentication").</summary>
    public const string Username = "admin";

    /// <summary>The <c>src</c> of every request frame, which the device sends back as <c>dst</c> ("RPC Protocol").</summary>
    public const string Source = "contactor";

    /// <summary>"returns information about the device"; answers without a password ("Authentication").</summary>
    public const string GetDeviceInfo = "Shelly.GetDeviceInfo";

    /// <summary>"returns the status of all the components of the device" ("Shelly").</summary>
    public const string GetStatus = "Shelly.GetStatus";

    /// <summary>"sets the output of the Switch component to on or off": <c>{"id":n,"on":bool}</c> ("Switch").</summary>
    public const string SwitchSet = "Switch.Set";

    /// <summary>"to obtain the component's configuration": <c>{"id":n}</c> ("Switch", "Switch.GetConfig").</summary>
    public const string SwitchGetConfig = "Switch.GetConfig";

    /// <summary>"to update the component's configuration": <c>{"id":n,"config":{…}}</c> ("Switch", "Switch.SetConfig").</summary>
    public const string SwitchSetConfig = "Switch.SetConfig";

    /// <summary>
    /// The member of a switch's configuration that says what it does at power on: "Output state to set on power_on.
    /// Range of values: off, on, restore_last, match_input" ("Switch", "Configuration").
    /// </summary>
    public const string InitialState = "initial_state";

    /// <summary>
    /// The member of <c>sys</c> in <see cref="GetStatus"/>'s result that "will be incremented for every configuration
    /// change of a device component" ("Sys", "Configuration" and "Status").
    /// </summary>
    public const string ConfigRevision = "cfg_rev";

    /// <summary>
    /// The member of a switch's configuration that turns it on again by itself: "True if the \"Automatic ON\" function
    /// is enabled, false otherwise" ("Switch", "Configuration"). The app reads it and sets it to false only.
    /// </summary>
    public const string AutoOn = "auto_on";

    /// <summary>
    /// The member of a switch's configuration with the mode of its input, "Mode of the associated input": <c>momentary</c>,
    /// <c>follow</c>, <c>flip</c>, <c>detached</c>, <c>cycle</c> or <c>activate</c> ("Switch", "Configuration"). The app
    /// reads it, and sets it to <see cref="InputDetached"/> only.
    /// </summary>
    public const string InputMode = "in_mode";

    /// <summary>
    /// The member of a switch's configuration that locks its input: "If True, all changes to physical inputs are ignored,
    /// regardless of mode" ("Switch", "Configuration").
    /// </summary>
    public const string InputLocked = "in_locked";

    /// <summary>The <see cref="InputMode"/> of an input that switches nothing: the one the app sets.</summary>
    public const string InputDetached = "detached";

    /// <summary>"toggles the output state": <c>{"id":n}</c> ("Switch", "Switch.Toggle"). The app only reads it in a schedule.</summary>
    public const string SwitchToggle = "Switch.Toggle";

    /// <summary>"sets the output state of all Switch components": <c>{"on":bool}</c> ("Switch", "Switch.SetAll"). Only read in a schedule.</summary>
    public const string SwitchSetAll = "Switch.SetAll";

    /// <summary>"applies the same state change to multiple Switch components": <c>{"ids":[n,…],"on":bool}</c> ("Switch", "Switch.SetMany"). Only read in a schedule.</summary>
    public const string SwitchSetMany = "Switch.SetMany";

    /// <summary>
    /// "lists all available RPC methods. It takes into account both ACL and authentication restrictions and only lists
    /// the methods allowed for the particular user/channel that's making the request": its result has <c>methods</c>,
    /// an array of strings ("Shelly", "Shelly.ListMethods"). Asked once a connection, to tell whether the device has
    /// <see cref="ScheduleList"/>.
    /// </summary>
    public const string ListMethods = "Shelly.ListMethods";

    /// <summary>
    /// The most method names the app reads from <see cref="ListMethods"/>; an answer with more is refused. The docs give
    /// no limit: this is the app's own. The docs' example of a Pro 4PM lists 59.
    /// </summary>
    public const int MaxMethods = 1000;

    /// <summary>"lists all existing schedules for this device": its result has <c>jobs</c> and <c>rev</c> ("Schedule", "Schedule.List").</summary>
    public const string ScheduleList = "Schedule.List";

    /// <summary>"updates an existing schedule job": <c>{"id":n,"enable":false}</c> ("Schedule", "Schedule.Update").</summary>
    public const string ScheduleUpdate = "Schedule.Update";

    /// <summary>
    /// The member of <c>sys</c> in <see cref="GetStatus"/>'s result with the "Schedules revision number, present if
    /// schedules are enabled" ("Sys", "Status"), which "is incremented on every update of the schedules" ("Schedule").
    /// </summary>
    public const string ScheduleRevision = "schedule_rev";

    /// <summary>"There is a limit of 20 schedule instances per device" ("Schedule"): a list with more is refused.</summary>
    public const int MaxScheduleJobs = 20;

    /// <summary>"There is a limit of 5 calls per schedule job" ("Schedule"): a job with more is refused.</summary>
    public const int MaxScheduleCalls = 5;

    /// <summary>
    /// What the app knows of a device's own rules: it reads its schedules that switch a relay, each switch's auto-on and
    /// each switch's input mode, and turns them off; it can't see scripts, webhooks, MQTT, the cloud or auto-off, so a
    /// person should still check the device's own page.
    /// </summary>
    public static OwnRulesAbility OwnRules { get; } = new(Reads: true, TurnsOff: true, MayHaveUnseen: true);

    /// <summary>
    /// The rule a switch's <see cref="InputMode"/> makes, as <see cref="ShellyInputRule"/>: <c>flip</c> and <c>follow</c>
    /// switch the relay at each change of the input (<see cref="OwnRuleKind.InputEdge"/>); <c>momentary</c>,
    /// <c>cycle</c> and <c>activate</c> switch it, or turn it on, at a press (<see cref="OwnRuleKind.InputToggle"/>;
    /// <c>cycle</c> steps both relays of a 2-relay device, "SW:0-ON &amp;&amp; SW:1-OFF, SW:0-OFF &amp;&amp; SW:1-ON,
    /// SW:0-ON &amp;&amp; SW:1-ON, SW:0-OFF &amp;&amp; SW:1-OFF", "Cycle in_mode explanation", so it is marked
    /// <see cref="ShellyInputRule.Cycle"/>);
    /// <c>detached</c>, or any mode while <paramref name="locked"/>, switches nothing. Null for a mode the docs don't
    /// name. The device still obeys the app in every mode, so none of them is <see cref="OwnRuleKind.InputLinkage"/>.
    /// </summary>
    public static ShellyInputRule? ToInputRule(string? mode, bool locked)
    {
        var rule = mode switch
        {
            "flip" or "follow" => new ShellyInputRule(OwnRuleKind.InputEdge),
            "momentary" or "activate" => new ShellyInputRule(OwnRuleKind.InputToggle),
            "cycle" => new ShellyInputRule(OwnRuleKind.InputToggle, Cycle: true),
            InputDetached => ShellyInputRule.None,
            _ => (ShellyInputRule?)null,
        };
        return locked && rule is not null ? ShellyInputRule.None : rule;
    }

    /// <summary>
    /// What a switch's <see cref="InitialState"/> does after a power cut, all four the docs list (per relay).
    /// </summary>
    public static PowerOnAbility PowerOn { get; } = new([PowerOnState.Off, PowerOnState.AsBefore, PowerOnState.On, PowerOnState.FollowsInput], perRelay: true);

    /// <summary>
    /// An <see cref="InitialState"/> value as a state: <c>off</c>, <c>restore_last</c>, <c>on</c> and <c>match_input</c>;
    /// null for any other value, which the docs don't list.
    /// </summary>
    public static PowerOnState? ToPowerOnState(string? initialState)
    {
        return initialState switch
        {
            "off" => PowerOnState.Off,
            "restore_last" => PowerOnState.AsBefore,
            "on" => PowerOnState.On,
            "match_input" => PowerOnState.FollowsInput,
            _ => null,
        };
    }

    /// <summary>A state as its <see cref="InitialState"/> value; throws for a value that isn't a state.</summary>
    public static string ToInitialState(PowerOnState state)
    {
        return state switch
        {
            PowerOnState.Off => "off",
            PowerOnState.AsBefore => "restore_last",
            PowerOnState.On => "on",
            PowerOnState.FollowsInput => "match_input",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state after a power cut."),
        };
    }

    /// <summary>The key of a switch's status in <see cref="GetStatus"/>'s result: <c>switch:0</c> is relay 1.</summary>
    public const string SwitchKeyPrefix = "switch:";

    /// <summary>The most relays a catalogue entry has: the Pro 4PM's four.</summary>
    public const int MaxRelayCount = 4;

    /// <summary>
    /// The key of an input's status in <see cref="GetStatus"/>'s result ("Input": "identified with input:&lt;id&gt;"):
    /// <c>input:0</c> is input 1.
    /// </summary>
    public const string InputKeyPrefix = "input:";

    /// <summary>
    /// The most digital inputs the app reads of a device: <c>input:0</c> to <c>input:3</c>, as many as the Pro 4PM has
    /// ("Shelly Pro 4 PM": "4 instances of Input"). The app's own limit: an input with a higher id isn't shown.
    /// </summary>
    public const int MaxInputCount = 4;

    /// <summary>"-109: FAILED PRECONDITION", e.g. "Overtemp condition present" ("Common Errors").</summary>
    public const int FailedPrecondition = -109;

    /// <summary>
    /// The longest password the app takes. The docs give no limit: this is the app's own, so a typing mistake can't
    /// grow without end. The password only goes into a SHA-256 hash, so any character works.
    /// </summary>
    public const int PasswordMaxLength = 128;

    /// <summary>
    /// The largest answer the app reads, 64 kB. The docs' own <c>Shelly.GetStatus</c> of a Pro 4PM is about 3 kB;
    /// a device with add-ons and scripts has more, but nothing near this.
    /// </summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>The oldest firmware the app takes: the Plus line stays on 1.7.x ("Firmware Update Policy").</summary>
    public static Version MinFirmware { get; } = new(1, 7, 0);

    /// <summary>
    /// From this firmware on, one nonce works for many requests with a growing <c>nc</c>; before it, "each HTTP request
    /// required going through the full 401 challenge-response cycle" ("Authentication").
    /// </summary>
    public static Version NonceReuseFirmware { get; } = new(2, 0, 0);

    /// <summary>
    /// The firmware version in <c>ver</c>, e.g. "1.7.1" or "2.0.0-beta1": its numbers before any "-", or null when it
    /// isn't one.
    /// </summary>
    public static Version? ParseFirmware(string? ver)
    {
        if (string.IsNullOrEmpty(ver) || ver.Length > 64)
        {
            return null;
        }

        var dash = ver.IndexOf('-', StringComparison.Ordinal);
        var numbers = dash < 0 ? ver : ver[..dash];
        var parts = numbers.Split('.');
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        var values = parts.Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new Version(values[0], values[1], values.Length == 3 ? values[2] : 0);
    }
}
