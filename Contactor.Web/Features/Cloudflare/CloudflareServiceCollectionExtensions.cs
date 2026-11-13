using Contactor.Web.Features.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

public static class CloudflareServiceCollectionExtensions
{
    /// <summary>
    /// What Cloudflare has as a way in and a sign-in: its settings (saved in the setup guide, or the app's settings),
    /// the tunnel the app runs itself when it has a token, and Cloudflare Access's key cache and token check.
    /// </summary>
    public static IServiceCollection AddCloudflare(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TunnelOptions>()
            .Bind(configuration.GetSection(TunnelOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICloudflaredProcess, CloudflaredProcess>();
        services.AddSingleton<CloudflareSettings>();
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

    /// <summary>
    /// At start: reads Cloudflare's settings, and refuses to start while Cloudflare Access is the sign-in and its team
    /// domain or AUD tag can't be used, rather than let people in unchecked. Call after the sign-in is read.
    /// </summary>
    public static async Task ApplyCloudflareSettingsAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = services.GetRequiredService<CloudflareSettings>();
        await settings.LoadAsync(cancellationToken);
        if (services.GetRequiredService<SignInChoice>().Current?.Id == CloudflareMethods.AccessId
            && !services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value.UsesDevUser
            && settings.Access.Problem is { } problem)
        {
            throw new InvalidOperationException(
                $"Cloudflare Access is the sign-in, but its settings can't be used: {problem} Save them in the setup guide, or set CloudflareAccess:TeamDomain and CloudflareAccess:Audience when the guide hasn't saved any.");
        }
    }
}
