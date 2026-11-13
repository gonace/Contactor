using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
using Contactor.Boards.Waveshare;

namespace Contactor.Simulator;

/// <summary>A model the simulator can pretend to be: what it is, which logins it takes, and how to make one.</summary>
/// <param name="Model">The model, as the app's board catalogue knows it.</param>
/// <param name="CheckLogin">Why the board can't take this user name and password, or null when it can.</param>
/// <param name="Create">A new board, not started; with a login, or none when <paramref name="Create"/> gets nulls.</param>
/// <param name="SuggestedPort">
/// The port the page offers for a new one: the model's own, unless that needs rights a developer doesn't have (below
/// 1024, e.g. the VM204's and VM208's 80).
/// </param>
public sealed record SimulatorModel(
    BoardModel Model,
    Func<string, string, string?> CheckLogin,
    Func<string?, string?, ILogger, TimeProvider, ISimulatedBoard> Create,
    int? SuggestedPort = null)
{
    public int Port => SuggestedPort ?? Model.DefaultPort;
}

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
                return error;
            },
            (username, password, logger, time) => new Vm201Simulator(username, password, logger) { TimeProvider = time }),
        new(
            Vm204Model.Model,
            (username, password) =>
            {
                Vm204Login.TryCreate(username, password, out var error);
                return error;
            },
            (username, password, logger, time) => new Vm204Simulator(username, password, logger) { TimeProvider = time },
            SuggestedPort: 8204),
        new(
            Vm208Model.Model,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The VM208 takes no login.",
            (_, _, logger, time) => new Vm208Simulator(logger) { TimeProvider = time },
            SuggestedPort: 8208),
        .. WaveshareModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length == 0 && password.Length == 0 ? null : "The Waveshare board takes no login.",
            (_, _, logger, time) => new WaveshareSimulator(model, logger) { TimeProvider = time })),
        .. DevantechModels.All.Select(model => new SimulatorModel(
            model,
            (username, password) => username.Length > 0
                ? "The Devantech board takes a password only, no user name."
                : DevantechModels.CheckPassword(password),
            (_, password, logger, time) => new DevantechSimulator(model, password, logger) { TimeProvider = time })),
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
        ArgumentNullException.ThrowIfNull(taken);
        for (var port = (Find(type) ?? All[0]).Port; port <= 65535; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        return null;
    }
}
