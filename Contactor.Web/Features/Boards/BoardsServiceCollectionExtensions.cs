using Contactor.Boards;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Boards;

public static class BoardsServiceCollectionExtensions
{
    /// <summary>
    /// The one board: the model <c>Board:Type</c> names (the VM201 when it is not set), its connection kept open by
    /// <see cref="BoardHostedService"/>, and what every board has in the app: its address and login, and the traffic
    /// for the Debug screen. Throws at start for a model the catalogue doesn't have.
    /// </summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var type = configuration.GetSection(BoardOptions.SectionName).Get<BoardOptions>()?.Type ?? BoardCatalogue.DefaultType;
        var registration = BoardCatalogue.Find(type)
            ?? throw new InvalidOperationException(
                $"Board:Type is \"{type}\", which isn't a board the app knows. Use one of: {string.Join(", ", BoardCatalogue.All.Select(r => r.Model.Id))}.");

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
