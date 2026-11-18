namespace Contactor.Boards.Shelly;

/// <summary>One answer to <c>Shelly.GetStatus</c>: which relays are on.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1 (<c>switch:0</c>); 1 = its <c>output</c> is true.</param>
public sealed record ShellyStatus(byte Relays);

/// <summary>
/// The device's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The device answered an authenticated <c>Shelly.GetStatus</c> on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the device last answered a poll, or null if it never has.</param>
/// <param name="Problem">Why the device refused the last connection: another device, or a password needed or refused.</param>
public sealed record ShellyState(bool IsConnected, ShellyStatus? Status, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null)
{
    public static ShellyState Initial { get; } = new(false, null, null);

    public byte Relays => Status?.Relays ?? 0;

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
