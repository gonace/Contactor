using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Boards.Waveshare;

namespace Contactor.Simulator;

/// <summary>Which login fields "Add a board" shows for a model: the ones its login has.</summary>
public enum SimulatorLoginFields
{
    /// <summary>The model has no login.</summary>
    None,

    /// <summary>A password alone, without a user name, e.g. a Shelly's or the DAEnetIP2 v2's community.</summary>
    Password,

    UsernameAndPassword,
}

/// <summary>A model the simulator can pretend to be: what it is, which logins it takes, and how to make one.</summary>
/// <param name="Model">The model, as the app's board catalogue knows it.</param>
/// <param name="CheckLogin">Why the board can't take this user name and password, or null when it can.</param>
/// <param name="Create">A new board, not started; with a login, or none when <paramref name="Create"/> gets nulls.</param>
/// <param name="SuggestedPort">
/// The port the page offers for a new one: the model's own, unless that needs rights a developer doesn't have (below
/// 1024, e.g. the VM204's, VM208's and Shelly's 80).
/// </param>
public sealed record SimulatorModel(
    BoardModel Model,
    Func<string, string, string?> CheckLogin,
    Func<string?, string?, ILogger, TimeProvider, ISimulatedBoard> Create,
    int? SuggestedPort = null)
{
    public int Port => SuggestedPort ?? Model.DefaultPort;

    /// <summary>What the app speaks to it, e.g. "Modbus TCP" or "HTTP JSON-RPC", for "Add a board".</summary>
    public required string Protocol { get; init; }

    /// <summary>How often the app asks a board that doesn't report changes by itself, by default (the client's options).</summary>
    public int PollSeconds { get; init; }

    /// <summary>What its own rules are, e.g. "timers per relay"; null for a model whose rules the app can't read.</summary>
    public string? OwnRulesName { get; init; }

    /// <summary>How the app talks to it, e.g. "TCP packets, pushes changes" or "HTTP, polled every 2 s".</summary>
    public string Talks => Model.Capabilities.ReportsChanges
        ? $"{Protocol}, pushes changes"
        : string.Create(CultureInfo.InvariantCulture, $"{Protocol}, polled every {PollSeconds} s");

    /// <summary>Its port: the real board's, and the simulator's when that is another, e.g. "80 → 8204".</summary>
    public string PortText => Port == Model.DefaultPort
        ? Port.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Model.DefaultPort} → {Port}");

    /// <summary>The model's own rules, the state after a power cut and its pulse, in one line from its capabilities.</summary>
    public string Rules
    {
        get
        {
            var capabilities = Model.Capabilities;
            var rules = capabilities.OwnRulesOrCantTell;
            var name = OwnRulesName is null ? "Own rules" : $"Own rules: {OwnRulesName}";
            List<string> parts =
            [
                rules switch
                {
                    { Reads: false, MayHaveUnseen: false } => "No own rules.",
                    { Reads: false } => "Own rules: the app can't tell whether it has any.",
                    { TurnsOff: true, MayHaveUnseen: true } => $"{name} (the app reads them and can turn them off; there may be others it can't see).",
                    { TurnsOff: true } => $"{name} (the app reads them and can turn them off).",
                    { MayHaveUnseen: true } => $"{name} (the app reads them; there may be others it can't see).",
                    _ => $"{name} (the app reads them).",
                },
            ];
            if (capabilities.PowerOn is { } powerOn)
            {
                parts.Add(powerOn.PerRelay ? "Power-on state per relay." : "One power-on state for the whole board.");
            }

            parts.Add(capabilities.Pulse is { } pulse ? $"Pulse {Length(pulse.Shortest)} – {Length(pulse.Longest)}." : "No pulse.");
            return string.Join(" ", parts);
        }
    }

    /// <summary>The login fields "Add a board" shows: the ones the model's login has.</summary>
    public SimulatorLoginFields LoginFields => !Model.HasLogin
        ? SimulatorLoginFields.None
        : Model.PasswordOnly ? SimulatorLoginFields.Password : SimulatorLoginFields.UsernameAndPassword;

    /// <summary>The model's login limits in words, and what an empty login means.</summary>
    public string LoginText
    {
        get
        {
            var hint = Model.LoginHint;
            var max = Model.LoginMaxLength;
            return LoginFields switch
            {
                SimulatorLoginFields.None => "The board takes no login.",
                SimulatorLoginFields.Password when hint is { Kind: LoginHintKind.SnmpCommunity } => string.Create(
                    CultureInfo.InvariantCulture,
                    $"Its read-write community, {hint.MinLength} to {hint.MaxLength} characters. Empty: the factory one, {hint.FactoryValue}."),
                SimulatorLoginFields.Password => string.Create(
                    CultureInfo.InvariantCulture,
                    $"A password only, at most {max} characters. Empty: the board asks for no password."),
                _ when hint is { TotalMaxLength: > 0 } => string.Create(
                    CultureInfo.InvariantCulture,
                    $"At most {max} characters each, {hint.TotalMaxLength} together. Empty: the board asks for no login."),
                _ => string.Create(CultureInfo.InvariantCulture, $"At most {max} characters each. Empty: the board asks for no login."),
            };
        }
    }

    // "1 s", "99 h", "0.1 s" or "3,276.7 s": in the largest unit it is a whole number of, otherwise in seconds.
    private static string Length(TimeSpan length)
    {
        if (length.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{length.Ticks / TimeSpan.TicksPerHour} h");
        }

        if (length.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{length.Ticks / TimeSpan.TicksPerMinute} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{length.TotalSeconds:#,0.###} s");
    }
}

/// <summary>A manufacturer in "Add a board": its name, and its models in alphabetical order.</summary>
public sealed record SimulatorManufacturer(string Name, IReadOnlyList<SimulatorModel> Models);

/// <summary>Every model the simulator knows. A new model's project brings its simulator; it gets one line here.</summary>
public static class SimulatorCatalogue
{
    public static IReadOnlyList<SimulatorModel> All { get; } =
    [
        new(
            Vm201Model.Model,
            (username, password) =>
            {
                Vm201Login.TryCreate(username, password, out var error);
                return error?.ToString();
            },
            (username, password, logger, time) => new Vm201Simulator(username, password, logger) { TimeProvider = time })
        {
            Protocol = "TCP packets",
            OwnRulesName = "timers per relay",
        },
        new(
            Vm204Model.Model,
            (username, password) =>
            {
                Vm204Login.TryCreate(username, password, out var error);
                return error?.ToString();
            },
            (username, password, logger, time) => new Vm204Simulator(username, password, logger) { TimeProvider = time },
            SuggestedPort: 8204)
        {
            Protocol = "HTTP",
            PollSeconds = new Vm204Options().PollSeconds,
            OwnRulesName = "a pulse started on the board's own page",
        },
        new(
            Vm208Model.Model,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The VM208 takes no login.",
            (_, _, logger, time) => new Vm208Simulator(logger) { TimeProvider = time },
            SuggestedPort: 8208)
        {
            Protocol = "HTTP",
            PollSeconds = new Vm208Options().PollSeconds,
            OwnRulesName = "a week schedule per relay",
        },
        .. WaveshareModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The Waveshare board takes no login.",
            (_, _, logger, time) => new WaveshareSimulator(model, logger) { TimeProvider = time })
        {
            Protocol = "Modbus RTU over TCP",
            PollSeconds = new WaveshareOptions().PollSeconds,
            OwnRulesName = "relay modes that follow the inputs",
        }),
        new(
            SmartDenModels.Ip16RMt,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The Denkovi board takes no login.",
            (_, _, logger, time) => new SmartDenSimulator(logger) { TimeProvider = time },
            SuggestedPort: 8502)
        {
            Protocol = "Modbus TCP",
            PollSeconds = new SmartDenOptions().PollSeconds,
            OwnRulesName = "a week schedule of 30 rows",
        },
        .. DaenetIp2Models.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length > 0
                ? "The DAEnetIP2 v2 takes its read-write community as the password, no user name."
                : DaenetIp2Models.CheckCommunity(password)?.ToString(),
            (_, community, logger, time) => new DaenetIp2Simulator(model, community, logger) { TimeProvider = time },
            SuggestedPort: 8161)
        {
            Protocol = "SNMP over UDP",
            PollSeconds = new DaenetIp2Options().PollSeconds,
            OwnRulesName = "the ICMP monitoring's restart pins",
        }),
        .. DevantechModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length > 0
                ? "The Devantech board takes a password only, no user name."
                : DevantechModels.CheckPassword(password)?.ToString(),
            (_, password, logger, time) => new DevantechSimulator(model, password, logger) { TimeProvider = time })
        {
            Protocol = "TCP commands",
            PollSeconds = new DevantechOptions().PollSeconds,
        }),
        .. AdamModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The ADAM module takes no login.",
            (_, _, logger, time) => new AdamSimulator(model, logger) { TimeProvider = time },
            SuggestedPort: 8502)
        {
            Protocol = "Modbus TCP",
            PollSeconds = new AdamOptions().PollSeconds,
        }),
        .. ShellyModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length > 0
                ? "The Shelly takes a password only, no user name."
                : ShellyModels.CheckPassword(password)?.ToString(),
            (_, password, logger, time) => new ShellySimulator(model, password, logger) { TimeProvider = time },
            SuggestedPort: 8180)
        {
            Protocol = "HTTP JSON-RPC",
            PollSeconds = new ShellyOptions().PollSeconds,
            OwnRulesName = "schedules and auto-on",
        }),
    ];

    /// <summary>
    /// The manufacturers in alphabetical order, each with its models in alphabetical order, for "Add a board"; a number
    /// in a name counts as a number, so a 2-relay kit comes before a 16-relay one.
    /// </summary>
    public static IReadOnlyList<SimulatorManufacturer> Manufacturers { get; } =
    [
        .. All
            .GroupBy(m => m.Model.Brand, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SimulatorManufacturer(g.Key, [.. g.OrderBy(m => m.Model.ModelName, NameOrder.Instance)])),
    ];

    /// <summary>The model with this id, ignoring case and spaces around it, or null.</summary>
    public static SimulatorModel? Find(string? type)
    {
        var id = type?.Trim() ?? "";
        return All.FirstOrDefault(m => string.Equals(m.Model.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The port the page offers for a new board of this model: its suggested port, or the first one after it that no
    /// board here uses; null when every one up to 65535 is taken. An unknown model counts as the first one.
    /// </summary>
    public static int? FreePort(string? type, IReadOnlyCollection<int> taken)
    {
        return FreePort(type, taken, null);
    }

    /// <summary>
    /// As <see cref="FreePort(string?, IReadOnlyCollection{int})"/>, but with <paramref name="only"/> set, the first port
    /// in it that no board here uses, whatever the model; null when every one is taken.
    /// </summary>
    public static int? FreePort(string? type, IReadOnlyCollection<int> taken, PortRange? only)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var (first, last) = only is null ? ((Find(type) ?? All[0]).Port, 65535) : (only.First, only.Last);
        for (var port = first; port <= last; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        return null;
    }

    // Names in alphabetical order, ignoring case, with each run of digits compared as a number: "2-relay" before "16-relay".
    // Our own, so the order is the same on every computer, also one without culture data.
    private sealed class NameOrder : IComparer<string>
    {
        public static NameOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            var (i, j) = (0, 0);
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var (startX, startY) = (i, j);
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                    {
                        j++;
                    }

                    // Without leading zeros, the longer run is the larger number; of two as long, the first digit that differs decides.
                    var digitsX = x.AsSpan(startX, i - startX).TrimStart('0');
                    var digitsY = y.AsSpan(startY, j - startY).TrimStart('0');
                    var byNumber = digitsX.Length != digitsY.Length
                        ? digitsX.Length.CompareTo(digitsY.Length)
                        : digitsX.SequenceCompareTo(digitsY);
                    if (byNumber != 0)
                    {
                        return byNumber;
                    }

                    continue;
                }

                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }

            var byLength = (x.Length - i).CompareTo(y.Length - j);
            return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
        }
    }
}
