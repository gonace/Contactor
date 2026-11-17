using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Inputs;

public static class InputsServiceCollectionExtensions
{
    /// <summary>The board's digital inputs on the Relays and Input screens, the relays and groups they control, and its analog inputs.</summary>
    public static IServiceCollection AddInputs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // The inputs' words, which take a relay status's times. AddLanguages also adds the localization; adding it twice
        // is harmless.
        services.AddLocalization();
        services.TryAddSingleton<RelayStatusText>();
        services.TryAddSingleton<InputsText>();
        services.AddSingleton<InputsHandler>();
        services.AddSingleton<AnalogInputsHandler>();
        services.AddSingleton<InputActionsHandler>();
        services.AddSingleton<InputChangeFeed>();
        services.AddSingleton<InputActionService>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<InputActionService>());
        services.AddHostedService(sp => sp.GetRequiredService<InputActionService>());
        return services;
    }
}
