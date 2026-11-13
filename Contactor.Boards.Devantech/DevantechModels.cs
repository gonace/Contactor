using System.Globalization;

namespace Contactor.Boards.Devantech;

/// <summary>How a Devantech model lays out what it sends.</summary>
/// <param name="Family">The name the board's module id stands for, e.g. "ETH008": the -B revision has the same id.</param>
/// <param name="ModuleId">What the board answers first to 0x10.</param>
/// <param name="OutputBytes">How many bytes 0x24 answers, low byte first: relay 1 is bit 0 of the first.</param>
/// <param name="InputBytes">How many bytes 0x25 answers, high byte first: input 1 is bit 0 of the last. 0: no 0x25.</param>
public sealed record DevantechLayout(string Family, byte ModuleId, int OutputBytes, int InputBytes);

/// <summary>
/// The Devantech ETH relay boards in the board catalogue. They speak the same binary commands on port 17494 and may have
/// a TCP password, which is all their login: there is no user name. Only their relays and digital inputs are read; the
/// analog inputs of the ETH484-B and ETH8020-B are issue #113. None is checked on a real board yet.
/// </summary>
public static class DevantechModels
{
    public static BoardModel Eth002 { get; } = Create("devantech-eth002", "Devantech ETH002-B", relays: 2, inputs: 0, analog: 0);

    public static BoardModel Eth008 { get; } = Create("devantech-eth008", "Devantech ETH008-B", relays: 8, inputs: 0, analog: 0);

    public static BoardModel Eth484 { get; } = Create("devantech-eth484", "Devantech ETH484-B", relays: 4, inputs: 8, analog: 4);

    public static BoardModel Eth8020 { get; } = Create("devantech-eth8020", "Devantech ETH8020-B", relays: 20, inputs: 8, analog: 8);

    public static IReadOnlyList<BoardModel> All { get; } = [Eth002, Eth008, Eth484, Eth8020];

    // The ETH484's 2 output bytes: relays 1–4 in the first, its I/O used as outputs in the second. The ETH8020's
    // 4 input bytes: the first three are always 0.
    private static readonly Dictionary<string, DevantechLayout> Layouts = new(StringComparer.Ordinal)
    {
        [Eth002.Id] = new("ETH002", 18, OutputBytes: 1, InputBytes: 0),
        [Eth008.Id] = new("ETH008", 19, OutputBytes: 1, InputBytes: 0),
        [Eth484.Id] = new("ETH484", 20, OutputBytes: 2, InputBytes: 2),
        [Eth8020.Id] = new("ETH8020", 21, OutputBytes: 3, InputBytes: 4),
    };

    // Module ids of Devantech's other ETH boards, only to say which one is at an address. From Devantech's own code
    // and others'; the app supports none of them.
    private static readonly Dictionary<byte, string> OtherModules = new()
    {
        [16] = "ETH-RLY16",
        [17] = "ETH-RLY02",
        [23] = "ETH0621",
        [29] = "ETH044",
        [51] = "ETH1620",
        [52] = "ETH1610",
        [54] = "ETH24V008H",
    };

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsDevantech(BoardModel model)
    {
        return All.Contains(model);
    }

    /// <summary>How <paramref name="model"/> lays out its answers.</summary>
    public static DevantechLayout Layout(BoardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return IsDevantech(model) && Layouts.TryGetValue(model.Id, out var layout)
            ? layout
            : throw new ArgumentException($"{model.Name} isn't a Devantech board.", nameof(model));
    }

    /// <summary>The board a module id stands for, e.g. "ETH484", or null for an id the app doesn't know.</summary>
    public static string? ModuleName(byte moduleId)
    {
        return Layouts.Values.FirstOrDefault(l => l.ModuleId == moduleId)?.Family
            ?? OtherModules.GetValueOrDefault(moduleId);
    }

    /// <summary>
    /// Why the board at the address isn't <paramref name="model"/>, in words for the Debug screen, e.g. "The board at
    /// this address is an ETH484, not an ETH008."; null when it is.
    /// </summary>
    public static string? CheckModule(BoardModel model, byte moduleId)
    {
        var expected = Layout(model);
        if (moduleId == expected.ModuleId)
        {
            return null;
        }

        return ModuleName(moduleId) is { } name
            ? $"The board at this address is an {name}, not an {expected.Family}."
            : string.Create(CultureInfo.InvariantCulture, $"The board at this address says it is module {moduleId}, not an {expected.Family}.");
    }

    /// <summary>Why the board can't take this password, in words for the person who typed it, or null when it can.</summary>
    public static string? CheckPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > DevantechApi.PasswordMaxLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The password can be at most {DevantechApi.PasswordMaxLength} characters.");
        }

        return password.All(DevantechApi.IsPasswordCharacter)
            ? null
            : "The password can only contain letters a–z and A–Z, digits and plain symbols, without spaces.";
    }

    private static BoardModel Create(string id, string name, int relays, int inputs, int analog)
    {
        return new BoardModel(
            id,
            name,
            new BoardCapabilities(relays, inputs, ReportsChanges: false, AnalogInputCount: analog),
            DevantechApi.DefaultPort,
            LoginMaxLength: DevantechApi.PasswordMaxLength,
            LoginHint: string.Create(
                CultureInfo.InvariantCulture,
                $"The TCP password from the board's configuration page, not the login of its web page. At most {DevantechApi.PasswordMaxLength} characters: letters, digits and plain symbols."),
            PasswordOnly: true);
    }
}
