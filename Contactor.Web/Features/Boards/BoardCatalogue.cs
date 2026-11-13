using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Devantech;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Features.Vm204;
using Contactor.Web.Features.Vm208;
using Contactor.Web.Features.Waveshare;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// A model of board the app supports, and how to build it while the app runs: its <see cref="IRelayBoard"/>,
/// <see cref="IBoardConnection"/> and <see cref="IBoardTraffic"/>, and anything only it has.
/// </summary>
public sealed record BoardRegistration(BoardModel Model, Func<BoardModel, IServiceProvider, IConfiguration, ActiveBoard> Create);

/// <summary>A make of board and its models, as the setup guide lists them.</summary>
public sealed record BoardMake(string Name, IReadOnlyList<BoardModel> Models);

/// <summary>
/// Every model of board the app supports. A new model is a project of its own with its adapters, and one line here.
/// </summary>
public static class BoardCatalogue
{
    /// <summary>The model an installation in use from before the setup guide chose its model has: see <see cref="BoardChoice"/>.</summary>
    public const string EarlierDefaultType = "vm201";

    public static IReadOnlyList<BoardRegistration> All { get; } =
    [
        new(Vm201Model.Model, Vm201Boards.Create),
        new(Vm204Model.Model, Vm204Boards.Create),
        new(Vm208Model.Model, Vm208Boards.Create),
        new(WaveshareModels.Relay8, WaveshareBoards.Create),
        new(WaveshareModels.Relay8B, WaveshareBoards.Create),
        new(WaveshareModels.Relay8C, WaveshareBoards.Create),
        new(WaveshareModels.Relay16, WaveshareBoards.Create),
        new(WaveshareModels.Relay30, WaveshareBoards.Create),
        new(DevantechModels.Eth002, DevantechBoards.Create),
        new(DevantechModels.Eth008, DevantechBoards.Create),
        new(DevantechModels.Eth484, DevantechBoards.Create),
        new(DevantechModels.Eth8020, DevantechBoards.Create),
    ];

    /// <summary>The makes in alphabetical order, each with its models in the catalogue's order.</summary>
    public static IReadOnlyList<BoardMake> Makes { get; } =
    [
        .. All
            .GroupBy(r => r.Model.Make, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BoardMake(g.Key, [.. g.Select(r => r.Model)])),
    ];

    /// <summary>The model with this id, ignoring case and spaces around it, or null.</summary>
    public static BoardRegistration? Find(string? type)
    {
        var id = type?.Trim() ?? "";
        return All.FirstOrDefault(r => string.Equals(r.Model.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The ids, for a message about one the app doesn't know.</summary>
    public static string Ids => string.Join(", ", All.Select(r => r.Model.Id));
}
