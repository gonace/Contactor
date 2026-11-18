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

    /// <summary>The key of a switch's status in <see cref="GetStatus"/>'s result: <c>switch:0</c> is relay 1.</summary>
    public const string SwitchKeyPrefix = "switch:";

    /// <summary>The most relays a catalogue entry has: the Pro 4PM's four.</summary>
    public const int MaxRelayCount = 4;

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
