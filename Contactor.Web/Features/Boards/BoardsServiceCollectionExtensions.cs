using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Boards;

public static class BoardsServiceCollectionExtensions
{
    /// <summary>
    /// The boards, through <see cref="BoardRegistry"/>: one <see cref="LiveBoard"/> per board's row, none until the
    /// setup guide chooses one (<see cref="BoardChoice"/>), their connections kept open by
    /// <see cref="BoardHostedService"/>, and what every board has in the app: its address and login, and the
    /// Debugging setting.
    /// </summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BoardRegistry>();
        services.AddSingleton<BoardChoice>();
        services.AddSingleton<BoardAddressHandler>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<BoardHostedService>();
        return services;
    }
}
