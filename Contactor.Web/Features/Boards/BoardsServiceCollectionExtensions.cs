using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Boards;

public static class BoardsServiceCollectionExtensions
{
    /// <summary>
    /// The boards, through <see cref="BoardRegistry"/>: one <see cref="LiveBoard"/> per board's row, none until the
    /// setup guide chooses one (<see cref="BoardChoice"/>), their connections kept open by
    /// <see cref="BoardHostedService"/>, and what every board has in the app: its name, address and login, removing it
    /// (<see cref="BoardRemoval"/>), and the Debugging setting.
    /// </summary>
    public static IServiceCollection AddBoards(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<BoardAddressOptions>()
            .Bind(configuration.GetSection(BoardAddressOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BoardAddressOptions>, BoardAddressOptionsValidation>();
        services.AddSingleton<BoardAddressLimit>();
        services.AddSingleton<BoardRegistry>();
        services.AddSingleton<BoardChoice>();
        // The boards' messages. AddLanguages also adds the localization; adding it twice is harmless.
        services.AddLocalization();
        services.AddSingleton<BoardLoginText>();
        services.AddSingleton<BoardProblemText>();
        services.AddSingleton<BoardAddressHandler>();
        services.AddSingleton<BoardRemoval>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<BoardHostedService>();
        return services;
    }
}
