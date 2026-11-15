namespace Contactor.Web.Features.Setup;

public static class SetupServiceCollectionExtensions
{
    /// <summary>The setup guide a new installation opens on.</summary>
    public static IServiceCollection AddSetup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<SetupHandler>();
        services.AddSingleton<SetupCode>();
        services.AddSingleton<SetupMode>();
        // The guide's texts. AddLanguages also adds the localization; adding it twice is harmless.
        services.AddLocalization();
        services.AddSingleton<SetupText>();
        services.AddSingleton<SetupSignInHandler>();
        return services;
    }
}
