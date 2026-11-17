using System.Globalization;

namespace Contactor.Boards.Devantech;

/// <summary>How a Devantech model lays out what it sends.</summary>
/// <param name="Family">The name the board's module id stands for, e.g. "ETH008": the -B revision has the same id.</param>
/// <param name="ModuleId">What the board answers first to 0x10.</param>
/// <param name="OutputBytes">How many bytes 0x24 answers, low byte first: relay 1 is bit 0 of the first.</param>
/// <param name="InputBytes">
/// How many bytes 0x25 answers, high byte first: input 1 is bit 0 of the last. 0: no 0x25. The app asks for them only
/// on a model with inputs (<see cref="BoardCapabilities.InputCount"/>).
/// </param>
/// <param name="HasVolts">Whether the board answers 0x78, its supply voltage; the app doesn't ask, the simulator answers.</param>
/// <param name="AllAnalog">
/// Whether the board reads all its analog inputs with one 0x34; otherwise one 0x32 per input. Only on a model with
/// analog inputs (<see cref="BoardCapabilities.Analog"/>).
/// </param>
public sealed record DevantechLayout(string Family, byte ModuleId, int OutputBytes, int InputBytes, bool HasVolts, bool AllAnalog = false);

/// <summary>
/// The Devantech ETH relay boards in the board catalogue. They speak the same binary commands on port 17494 and may have
/// a TCP password, which is all their login: there is no user name. The app reads their relays, their digital inputs and
/// the analog inputs of the ETH1610, ETH484-B and ETH8020-B. The ETH1610's digital inputs aren't read: its manual
/// doesn't say which bit is which input. None is checked on a real board yet.
/// </summary>
public static class DevantechModels
{
    /// <summary>Who makes them: the setup guide lists the models by brand.</summary>
    public const string Brand = "Devantech";

    /// <summary>
    /// The pulse all five have (0x20 with a pulse time): 0.1 s to 25.5 s in steps of 0.1 s. Before the models, which
    /// take it.
    /// </summary>
    public static PulseAbility Pulse { get; } = new(DevantechApi.PulseUnit, DevantechApi.PulseUnit * DevantechApi.MaxPulseTime, DevantechApi.PulseUnit);

    public static BoardModel Eth002 { get; } = Create("devantech-eth002", "ETH002-B", relays: 2, inputs: 0, analog: null);

    public static BoardModel Eth008 { get; } = Create("devantech-eth008", "ETH008-B", relays: 8, inputs: 0, analog: null);

    // 10 bit, 0–5 V (ETH1610 Technical Documentation, 11 Feb 2026, "Analogue Inputs" p. 4).
    public static BoardModel Eth1610 { get; } = Create("devantech-eth1610", "ETH1610", relays: 10, inputs: 0, analog: new AnalogAbility(16, 1023, 5m));

    // 0x32: 10 bit, full scale at 3.3 V, as on the original ETH484 (ETH484-B Technical Documentation, 11 Feb 2026, p. 5, p. 18).
    public static BoardModel Eth484 { get; } = Create("devantech-eth484", "ETH484-B", relays: 4, inputs: 8, analog: new AnalogAbility(4, 1023, 3.3m));

    // 10 bit, 0–5 V (ETH8020-B Technical Documentation, 11 Feb 2026, "Analogue Inputs" p. 4).
    public static BoardModel Eth8020 { get; } = Create("devantech-eth8020", "ETH8020-B", relays: 20, inputs: 8, analog: new AnalogAbility(8, 1023, 5m));

    public static IReadOnlyList<BoardModel> All { get; } = [Eth002, Eth008, Eth1610, Eth484, Eth8020];

    // The ETH484's 2 output bytes: relays 1–4 in the first, its I/O used as outputs in the second. The ETH8020's
    // 4 input bytes: the first three are always 0. The ETH1610's 2 output bytes: relays 1–8 and 9–10; it answers 0x25
    // with 2 bytes for its 16 inputs, in an order its manual doesn't give, so the app doesn't ask (inputs: 0 above).
    // Only the ETH1610 has 0x34, all analog inputs at once (manual p. 18); the ETH484-B and ETH8020-B are read with a
    // 0x32 per input.
    private static readonly Dictionary<string, DevantechLayout> Layouts = new(StringComparer.Ordinal)
    {
        [Eth002.Id] = new("ETH002", 18, OutputBytes: 1, InputBytes: 0, HasVolts: true),
        [Eth008.Id] = new("ETH008", 19, OutputBytes: 1, InputBytes: 0, HasVolts: true),
        [Eth1610.Id] = new("ETH1610", 52, OutputBytes: 2, InputBytes: 2, HasVolts: false, AllAnalog: true),
        [Eth484.Id] = new("ETH484", 20, OutputBytes: 2, InputBytes: 2, HasVolts: true),
        [Eth8020.Id] = new("ETH8020", 21, OutputBytes: 3, InputBytes: 4, HasVolts: true),
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
    /// The model in the board catalogue a module id stands for, e.g. <see cref="Eth484"/> for 20; null for an id of a
    /// board the app doesn't support or doesn't know.
    /// </summary>
    public static BoardModel? ModelFor(byte moduleId)
    {
        return All.FirstOrDefault(m => Layouts[m.Id].ModuleId == moduleId);
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

    /// <summary>Why the board can't take this password, or null when it can; its English is its ToString.</summary>
    public static LoginProblem? CheckPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > DevantechApi.PasswordMaxLength)
        {
            return new LoginProblem(LoginProblemKind.PasswordTooLong, MaxLength: DevantechApi.PasswordMaxLength);
        }

        return password.All(DevantechApi.IsPasswordCharacter) ? null : new LoginProblem(LoginProblemKind.PasswordCharacters);
    }

    private static BoardModel Create(string id, string name, int relays, int inputs, AnalogAbility? analog)
    {
        return new BoardModel(
            id,
            Brand,
            name,
            new BoardCapabilities(relays, inputs, ReportsChanges: false, Analog: analog, Pulse: Pulse, OwnRules: OwnRulesAbility.CantTell),
            DevantechApi.DefaultPort,
            LoginMaxLength: DevantechApi.PasswordMaxLength,
            LoginHint: new LoginHint(LoginHintKind.TcpPassword, DevantechApi.PasswordMaxLength),
            PasswordOnly: true);
    }
}
