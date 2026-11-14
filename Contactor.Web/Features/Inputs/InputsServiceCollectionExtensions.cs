using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Inputs;

public static class InputsServiceCollectionExtensions
{
    /// <summary>The board's digital inputs on the Relays and Input screens, and the relays and groups they control.</summary>
    public static IServiceCollection AddInputs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<InputsHandler>();
        services.AddSingleton<InputActionsHandler>();
        services.AddSingleton<InputChangeFeed>();
        services.AddSingleton<InputActionService>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<InputActionService>());
        services.AddHostedService(sp => sp.GetRequiredService<InputActionService>());
        return services;
    }
}
