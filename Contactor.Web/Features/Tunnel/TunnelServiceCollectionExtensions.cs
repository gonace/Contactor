using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Tunnel;

public static class TunnelServiceCollectionExtensions
{
    /// <summary>The Cloudflare tunnel the app runs itself when <c>Tunnel:Token</c> is set.</summary>
    public static IServiceCollection AddTunnel(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TunnelOptions>()
            .Bind(configuration.GetSection(TunnelOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICloudflaredProcess, CloudflaredProcess>();
        services.AddHostedService<TunnelHostedService>();
        return services;
    }
}
