using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Relays;

public static class RelaysServiceCollectionExtensions
{
    public static IServiceCollection AddRelays(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<RelayChanges>();
        services.AddSingleton<RelayControl>();
        services.AddSingleton<RelaySettingsHandler>();
        // The texts these use; registered with the languages too, and once is enough.
        services.AddLocalization();
        services.AddSingleton<RelayStatusText>();
        services.AddSingleton<BoardsText>();
        services.AddSingleton<AutoOffService>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<AutoOffService>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoOffService>());
        return services;
    }
}
