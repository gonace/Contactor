namespace Contactor.Boards.Shelly;

/// <summary>
/// The Shelly Gen2+ devices in the board catalogue: one entry per relay count, 1 to 4, as the docs list no model codes
/// to tell them apart. Each relay is a Switch component (<c>switch:0</c> is relay 1). The login is the password of the
/// device's digest authentication; the user name is always "admin", so the screens ask for the password only. The
/// device's own schedules, auto-on and auto-off are rules the app can't see yet: <see cref="OwnRulesAbility.CantTell"/>.
/// </summary>
public static class ShellyModels
{
    /// <summary>Who makes them: the setup guide lists the models by brand.</summary>
    public const string Brand = "Shelly";

    public static BoardModel OneRelay { get; } = Create(1);

    public static BoardModel TwoRelays { get; } = Create(2);

    public static BoardModel ThreeRelays { get; } = Create(3);

    public static BoardModel FourRelays { get; } = Create(4);

    public static IReadOnlyList<BoardModel> All { get; } = [OneRelay, TwoRelays, ThreeRelays, FourRelays];

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsShelly(BoardModel model)
    {
        return All.Contains(model);
    }

    /// <summary>The entry for a device with this many switches, or null for a count the app has no entry for.</summary>
    public static BoardModel? ModelFor(int switchCount)
    {
        return switchCount is >= 1 and <= ShellyApi.MaxRelayCount ? All[switchCount - 1] : null;
    }

    /// <summary>Why the device can't take this password, or null when it can; its English is its ToString.</summary>
    public static LoginProblem? CheckPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return password.Length > ShellyApi.PasswordMaxLength
            ? new LoginProblem(LoginProblemKind.PasswordTooLong, MaxLength: ShellyApi.PasswordMaxLength)
            : null;
    }

    private static BoardModel Create(int relays)
    {
        var name = relays == 1 ? "1 relay (Gen2+)" : $"{relays} relays (Gen2+)";
        return new BoardModel(
            $"shelly-{relays}",
            Brand,
            name,
            new BoardCapabilities(relays, InputCount: 0, ReportsChanges: false, OwnRules: OwnRulesAbility.CantTell),
            ShellyApi.DefaultPort,
            LoginMaxLength: ShellyApi.PasswordMaxLength,
            PasswordOnly: true);
    }
}
