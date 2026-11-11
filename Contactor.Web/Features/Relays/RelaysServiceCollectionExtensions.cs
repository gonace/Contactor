namespace Contactor.Web.Features.Relays;

public static class RelaysServiceCollectionExtensions
{
    public static IServiceCollection AddRelays(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<RelayChanges>();
        services.AddSingleton<RelayControl>();
        services.AddSingleton<RelaySettingsHandler>();
        services.AddSingleton<AutoOffService>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoOffService>());
        return services;
    }
}
