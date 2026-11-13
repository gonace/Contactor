namespace Contactor.Web.Features.Setup;

public static class SetupServiceCollectionExtensions
{
    /// <summary>The setup guide a new installation opens on.</summary>
    public static IServiceCollection AddSetup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<SetupHandler>();
        return services;
    }
}
