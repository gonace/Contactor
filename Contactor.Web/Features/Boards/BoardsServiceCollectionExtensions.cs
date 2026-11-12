using Contactor.Boards;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Boards;

public static class BoardsServiceCollectionExtensions
{
    /// <summary>
    /// The one board: the model the setup guide saved, or else the one <c>Board:Type</c> names (the VM201 when it is
    /// not set), its connection kept open by <see cref="BoardHostedService"/>, and what every board has in the app:
    /// its address and login, and the traffic for the Debug screen. Throws at start for a model the catalogue doesn't
    /// have.
    /// </summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (PersistenceServiceCollectionExtensions.ReadSavedBoardType(configuration) is { } saved)
        {
            var chosen = BoardCatalogue.Find(saved)
                ?? throw new InvalidOperationException(
                    $"The relay board chosen in the setup guide is \"{saved}\", which isn't a board this version of the app knows. Use one of: {string.Join(", ", BoardCatalogue.All.Select(r => r.Model.Id))}.");
            return services.AddBoard(chosen, configuration);
        }

        var type = configuration.GetSection(BoardOptions.SectionName).Get<BoardOptions>()?.Type ?? BoardCatalogue.DefaultType;
        var registration = BoardCatalogue.Find(type)
            ?? throw new InvalidOperationException(
                $"Board:Type is \"{type}\", which isn't a board the app knows. Use one of: {string.Join(", ", BoardCatalogue.All.Select(r => r.Model.Id))}.");
        return services.AddBoard(registration, configuration);
    }

    private static IServiceCollection AddBoard(this IServiceCollection services, BoardRegistration registration, IConfiguration configuration)
    {
        services.AddSingleton(registration.Model);
        registration.Register(services, configuration);
        services.AddSingleton<BoardAddressHandler>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BoardTrace>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<BoardHostedService>();
        return services;
    }
}
