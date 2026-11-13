namespace Contactor.Web.Features.Access;

public static class AccessServiceCollectionExtensions
{
    /// <summary>The way in: which one the installation chose, and keeping it running.</summary>
    public static IServiceCollection AddAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<AccessChoice>();
        services.AddSingleton<LocalNetwork>();
        services.AddHostedService<AccessHostedService>();
        return services;
    }
}
