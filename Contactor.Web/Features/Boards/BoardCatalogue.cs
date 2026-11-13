using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Features.Vm204;
using Contactor.Web.Features.Vm208;
using Contactor.Web.Features.Waveshare;

namespace Contactor.Web.Features.Boards;

/// <summary>A model of board the app supports, and how to register it: its <see cref="IRelayBoard"/>,
/// <see cref="IBoardConnection"/> and <see cref="IBoardTraffic"/>, and anything only it has.</summary>
public sealed record BoardRegistration(BoardModel Model, Action<IServiceCollection, IConfiguration> Register);

/// <summary>
/// Every model of board the app supports. A new model is a project of its own with its adapters, and one line here.
/// </summary>
public static class BoardCatalogue
{
    public const string DefaultType = "vm201";

    public static IReadOnlyList<BoardRegistration> All { get; } =
    [
        new(Vm201Model.Model, (services, configuration) => services.AddVm201(configuration)),
        new(Vm204Model.Model, (services, configuration) => services.AddVm204(configuration)),
        new(Vm208Model.Model, (services, configuration) => services.AddVm208(configuration)),
        new(WaveshareModels.Relay8, (services, configuration) => services.AddWaveshare(configuration)),
        new(WaveshareModels.Relay8B, (services, configuration) => services.AddWaveshare(configuration)),
        new(WaveshareModels.Relay8C, (services, configuration) => services.AddWaveshare(configuration)),
        new(WaveshareModels.Relay16, (services, configuration) => services.AddWaveshare(configuration)),
        new(WaveshareModels.Relay30, (services, configuration) => services.AddWaveshare(configuration)),
    ];

    /// <summary>The model with this id, ignoring case and spaces around it, or null.</summary>
    public static BoardRegistration? Find(string? type)
    {
        var id = type?.Trim() ?? "";
        return All.FirstOrDefault(r => string.Equals(r.Model.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
