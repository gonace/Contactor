using Contactor.Boards;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Boards;

public static class BoardsServiceCollectionExtensions
{
    /// <summary>
    /// The board, through <see cref="BoardSwitch"/>: no board until one is chosen (<see cref="BoardChoice"/>), its
    /// connection kept open by <see cref="BoardHostedService"/>, and what every board has in the app: its address and
    /// login, and the traffic for the Debug screen.
    /// </summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BoardSwitch>();

        // The rest of the app only knows the board through these.
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<BoardSwitch>());
        services.AddSingleton<IBoardConnection>(provider => provider.GetRequiredService<BoardSwitch>());
        services.AddSingleton<IBoardTraffic>(provider => provider.GetRequiredService<BoardSwitch>());
        services.AddSingleton<BoardChoice>();
        services.AddSingleton<BoardAddressHandler>();
        services.AddSingleton<BoardTrace>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<BoardHostedService>();
        return services;
    }
}
