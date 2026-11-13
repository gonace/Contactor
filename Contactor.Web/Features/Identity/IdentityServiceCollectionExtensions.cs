namespace Contactor.Web.Features.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>The sign-in: which method the installation chose, <see cref="CurrentUser"/> and the people.</summary>
    public static IServiceCollection AddSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<SignInChoice>();
        services.AddScoped<CurrentUser>();
        services.AddSingleton<UsersHandler>();
        return services;
    }

    /// <summary>Denies every request the chosen sign-in doesn't let in. Add it before anything that serves content.</summary>
    public static IApplicationBuilder UseSignIn(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SignInMiddleware>();
    }
}
