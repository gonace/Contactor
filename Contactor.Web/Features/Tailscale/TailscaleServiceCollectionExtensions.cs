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

    /// <summary>
    /// Has the app also listen on Tailscale's socket (<see cref="TailscaleOptions.AppSocket"/>) when one is set, next to
    /// the addresses it has anyway (<c>ASPNETCORE_URLS</c>, else <c>ASPNETCORE_HTTP_PORTS</c> and
    /// <c>ASPNETCORE_HTTPS_PORTS</c>, else Kestrel's default). A socket file left by the last run is replaced.
    /// </summary>
    public static IWebHostBuilder ListenOnTailscaleSocket(this IWebHostBuilder webHost, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);
        var socket = configuration[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.AppSocket)}"];
        if (string.IsNullOrWhiteSpace(socket))
        {
            return webHost;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(socket))!);
        File.Delete(socket);
        return webHost.UseUrls([.. Addresses(configuration), $"http://unix:{socket}"]);
    }

    // The addresses Kestrel would listen on without the socket: setting any replaces them, so they are set too.
    public static IReadOnlyList<string> Addresses(IConfiguration configuration)
    {
        static IEnumerable<string> Split(string? value) =>
            (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var urls = Split(configuration["urls"]).ToList();
        if (urls.Count == 0)
        {
            urls.AddRange(Split(configuration["http_ports"]).Select(p => $"http://*:{p}"));
            urls.AddRange(Split(configuration["https_ports"]).Select(p => $"https://*:{p}"));
        }

        return urls.Count > 0 ? urls : ["http://localhost:5000"];
    }

    /// <summary>At start: reads Tailscale's settings.</summary>
    public static Task ApplyTailscaleSettingsAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<TailscaleSettings>().LoadAsync(cancellationToken);
    }
}
