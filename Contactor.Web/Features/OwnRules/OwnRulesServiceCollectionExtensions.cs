namespace Contactor.Web.Features.OwnRules;

public static class OwnRulesServiceCollectionExtensions
{
    /// <summary>
    /// A board's own rules (issue #497): what the app remembers and logs about them (<see cref="OwnRulesService"/>),
    /// and turning them off or keeping them (<see cref="OwnRulesHandler"/>). It listens to the boards beside the safety
    /// services, never as one of their watchers, so they never wait for it.
    /// </summary>
    public static IServiceCollection AddOwnRules(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddLocalization();
        services.AddSingleton<OwnRulesService>();
        services.AddHostedService(sp => sp.GetRequiredService<OwnRulesService>());
        services.AddSingleton<OwnRulesHandler>();
        return services;
    }
}
