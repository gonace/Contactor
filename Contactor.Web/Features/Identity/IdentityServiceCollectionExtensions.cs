namespace Contactor.Web.Features.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>The sign-in: which method the installation chose, <see cref="CurrentUser"/> and the people.</summary>
    public static IServiceCollection AddSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<SignInChoice>();
        services.AddKeyedSingleton<ISignInMethod, NoSignIn>(SignInCatalogue.NoneId);
        services.AddScoped<CurrentUser>();
        services.AddSingleton<UsersHandler>();
        return services;
    }

    /// <summary>
    /// At start, after the way in and the sign-in are read: refuses to start on a sign-in that doesn't fit the way in,
    /// e.g. No sign-in with a public link, or a sign-in without a way in.
    /// </summary>
    public static void CheckSignInFitsWayIn(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var signIn = services.GetRequiredService<SignInChoice>().Current;
        var wayIn = services.GetRequiredService<Access.AccessChoice>().Current;
        if (signIn is not null && (wayIn is null || !wayIn.SignIns.Contains(signIn.Id)))
        {
            throw new InvalidOperationException(
                $"The sign-in saved for this installation, {signIn.Name}, doesn't go with its way in, {wayIn?.Name ?? "none"}. Run reset-sign-in to choose again in the setup guide.");
        }
    }

    /// <summary>Denies every request the chosen sign-in doesn't let in. Add it before anything that serves content.</summary>
    public static IApplicationBuilder UseSignIn(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SignInMiddleware>();
    }
}
