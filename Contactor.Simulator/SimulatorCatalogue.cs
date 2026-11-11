using Contactor.Boards;
using Contactor.Boards.Vm201;

namespace Contactor.Simulator;

/// <summary>A model the simulator can pretend to be: what it is, which logins it takes, and how to make one.</summary>
/// <param name="Model">The model, as the app's board catalogue knows it.</param>
/// <param name="CheckLogin">Why the board can't take this user name and password, or null when it can.</param>
/// <param name="Create">A new board, not started; with a login, or none when <paramref name="Create"/> gets nulls.</param>
public sealed record SimulatorModel(
    BoardModel Model,
    Func<string, string, string?> CheckLogin,
    Func<string?, string?, ILogger, TimeProvider, ISimulatedBoard> Create);

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
    ];

    /// <summary>The model with this id, ignoring case and spaces around it, or null.</summary>
    public static SimulatorModel? Find(string? type)
    {
        var id = type?.Trim() ?? "";
        return All.FirstOrDefault(m => string.Equals(m.Model.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
