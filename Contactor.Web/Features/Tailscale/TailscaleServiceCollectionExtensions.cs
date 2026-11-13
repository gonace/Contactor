using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Tailscale;

public static class TailscaleServiceCollectionExtensions
{
    /// <summary>What Tailscale has as a way in: its settings, saved in the setup guide, and tailscaled, which the app runs itself.</summary>
    public static IServiceCollection AddTailscale(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TailscaleOptions>()
            .Bind(configuration.GetSection(TailscaleOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITailscaleProcess, TailscaleProcess>();
        services.AddSingleton<TailscaleSettings>();
        services.AddSingleton<TailscaleNetwork>();
        return services;
    }

    /// <summary>At start: reads Tailscale's settings.</summary>
    public static Task ApplyTailscaleSettingsAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<TailscaleSettings>().LoadAsync(cancellationToken);
    }
}
