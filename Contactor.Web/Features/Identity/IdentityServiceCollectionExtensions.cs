using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>Sign-in through Cloudflare Access: options, key cache, token validation and <see cref="CurrentUser"/>.</summary>
    public static IServiceCollection AddCloudflareAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<CloudflareAccessOptions>()
            .Bind(configuration.GetSection(CloudflareAccessOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CloudflareAccessOptions>, CloudflareAccessOptionsValidator>();
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName);
        services.AddSingleton<CloudflareAccessKeys>();
        services.AddSingleton<CloudflareAccessTokenValidator>();
        services.AddScoped<CurrentUser>();
        services.AddSingleton<UsersHandler>();
        return services;
    }

    /// <summary>Denies every request without a valid Access token. Add it before anything that serves content.</summary>
    public static IApplicationBuilder UseCloudflareAccess(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CloudflareAccessMiddleware>();
    }
}
