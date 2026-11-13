using Contactor.Web.Features.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Tailscale;

public static class TailscaleServiceCollectionExtensions
{
    /// <summary>
    /// What Tailscale has as a way in and a sign-in: its settings, saved in the setup guide, tailscaled, which the app
    /// runs itself, and Tailscale identity.
    /// </summary>
    public static IServiceCollection AddTailscale(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TailscaleOptions>()
            .Bind(configuration.GetSection(TailscaleOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITailscaleProcess, TailscaleProcess>();
        services.AddSingleton<TailscaleSettings>();
        services.AddSingleton<TailscaleNetwork>();
        services.AddSingleton<TailscaleIdentity>();
        services.AddKeyedSingleton<ISignInMethod>(TailscaleMethods.IdentityId, (s, _) => s.GetRequiredService<TailscaleIdentity>());
        return services;
    }

    /// <summary>
    /// Has the app also listen on Tailscale Serve's address (<see cref="TailscaleOptions.ServeAddress"/>) when one is
    /// set, next to the addresses it has anyway (<c>ASPNETCORE_URLS</c>, else <c>ASPNETCORE_HTTP_PORTS</c> and
    /// <c>ASPNETCORE_HTTPS_PORTS</c>, else Kestrel's default). One only Serve couldn't be alone on stops the app.
    /// </summary>
    public static IWebHostBuilder ListenForTailscaleServe(this IWebHostBuilder webHost, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);
        var address = configuration[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.ServeAddress)}"];
        if (string.IsNullOrWhiteSpace(address))
        {
            return webHost;
        }

        var endPoint = TailscaleOptions.ParseServeAddress(address)
            ?? throw new InvalidOperationException(
                $"Tailscale:ServeAddress \"{address}\" must be an IPv4 loopback address other than 127.0.0.1, with a port, e.g. 127.0.0.2:8081.");
        return webHost.UseUrls([.. Addresses(configuration), $"http://{endPoint}"]);
    }

    // The addresses Kestrel would listen on without Serve's: setting any replaces them, so they are set too.
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
