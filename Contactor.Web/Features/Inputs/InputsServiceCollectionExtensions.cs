namespace Contactor.Web.Features.Inputs;

public static class InputsServiceCollectionExtensions
{
    /// <summary>The board's digital inputs on the Relays and Input screens.</summary>
    public static IServiceCollection AddInputs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<InputsHandler>();
        return services;
    }
}
