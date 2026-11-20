namespace Contactor.Boards.Shelly;

/// <summary>
/// One answer to <c>Shelly.GetStatus</c>: which relays and inputs are on, with each switch's state after a power cut as
/// last read.
/// </summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1 (<c>switch:0</c>); 1 = its <c>output</c> is true.</param>
/// <param name="PowerOn">
/// Each switch's <c>initial_state</c> as last read with <c>Switch.GetConfig</c> (at connect, and when <c>cfg_rev</c>
/// changed); null before it was read.
/// </param>
/// <param name="OwnRules">
/// The device's own rules as last read: a <c>Timer</c> rule on each relay an enabled schedule switches, one unit per
/// schedule, and an <c>AutoOn</c> rule on each relay whose switch has <c>auto_on</c>. Null before they were read, and
/// when the last read of the schedules or of a switch's settings failed.
/// </param>
/// <param name="Inputs">Bit per input, bit 0 = input 1 (<c>input:0</c>); 1 = its <c>state</c> is true.</param>
/// <param name="InputCount">
/// How many inputs the device has for the app, 0 to <see cref="ShellyApi.MaxInputCount"/>: those with a state, from
/// <c>input:0</c> without a gap, as the connection's first status gave them (<see cref="ShellySwitches.InputCount"/>).
/// </param>
public sealed record ShellyStatus(byte Relays, PowerOnStates? PowerOn = null, OwnRules? OwnRules = null, byte Inputs = 0, int InputCount = 0)
{
    /// <summary>
    /// Compares the relays and the inputs only, for the poller: a change in <see cref="PowerOn"/> or
    /// <see cref="OwnRules"/> alone isn't a state change, and comes as <see cref="ShellyClient.PowerOnChanged"/> or
    /// <see cref="ShellyClient.OwnRulesChanged"/>.
    /// </summary>
    public static IEqualityComparer<ShellyStatus> StateComparer { get; } = new RelaysAndInputs();

    private sealed class RelaysAndInputs : IEqualityComparer<ShellyStatus>
    {
        public bool Equals(ShellyStatus? x, ShellyStatus? y)
        {
            return x is null
                ? y is null
                : y is not null && x.Relays == y.Relays && x.Inputs == y.Inputs && x.InputCount == y.InputCount;
        }

        public int GetHashCode(ShellyStatus obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return HashCode.Combine(obj.Relays, obj.Inputs, obj.InputCount);
        }
    }
}

/// <summary>
/// The device's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The device answered an authenticated <c>Shelly.GetStatus</c> on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the device last answered a poll, or null if it never has.</param>
/// <param name="Problem">
/// Why the device refused the last connection: another device or another number of relays, the cover profile, firmware
/// too old, a password needed or refused, or asked to wait (a 429).
/// </param>
public sealed record ShellyState(bool IsConnected, ShellyStatus? Status, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null)
{
    public static ShellyState Initial { get; } = new(false, null, null);

    public byte Relays => Status?.Relays ?? 0;

    /// <summary>The inputs that are on, bit 0 = input 1, kept while disconnected.</summary>
    public byte Inputs => Status?.Inputs ?? 0;

    /// <summary>How many inputs the device has for the app, kept while disconnected; 0 before it answered.</summary>
    public int InputCount => Status?.InputCount ?? 0;

    /// <summary>Each switch's state after a power cut as last read, kept while disconnected; null before it was read.</summary>
    public PowerOnStates? PowerOn => Status?.PowerOn;

    /// <summary>The device's own rules as last read, kept while disconnected; null when unknown.</summary>
    public OwnRules? OwnRules => Status?.OwnRules;

    public bool IsOn(int relay)
    {
        return relay is >= 1 and <= ShellyApi.MaxRelayCount && (Relays & (1 << (relay - 1))) != 0;
    }
}

/// <summary>What <c>Shelly.GetDeviceInfo</c> says about the device, as far as the app uses it.</summary>
/// <param name="Id">The device id, e.g. "shellypro4pm-f008d1d8b8b8"; also the digest realm.</param>
/// <param name="Model">The model code, e.g. "SPSW-004PE16EU"; null when not given.</param>
/// <param name="Generation">The generation, <c>gen</c>; null when not given, as on a Gen1 device.</param>
/// <param name="Firmware">The firmware version, <c>ver</c>, e.g. "1.7.1"; null when not given.</param>
/// <param name="Profile">The device profile, e.g. "switch" or "cover"; null on a device with one profile.</param>
public sealed record ShellyDeviceInfo(string Id, string? Model, int? Generation, string? Firmware, string? Profile);
