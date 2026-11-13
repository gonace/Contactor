using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

public static class CloudflareServiceCollectionExtensions
{
    /// <summary>
    /// What Cloudflare has as a way in and a sign-in: the tunnel the app runs itself when <c>Tunnel:Token</c> is set,
    /// and Cloudflare Access's options, key cache and token check.
    /// </summary>
    public static IServiceCollection AddCloudflare(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TunnelOptions>()
            .Bind(configuration.GetSection(TunnelOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICloudflaredProcess, CloudflaredProcess>();
        services.AddSingleton<CloudflareTunnel>();

        services.AddOptions<CloudflareAccessOptions>()
            .Bind(configuration.GetSection(CloudflareAccessOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CloudflareAccessOptions>, CloudflareAccessOptionsValidator>();
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName);
        services.AddSingleton<CloudflareAccessKeys>();
        services.AddSingleton<CloudflareAccessTokenValidator>();
        services.AddSingleton<CloudflareAccess>();
        return services;
    }
}
