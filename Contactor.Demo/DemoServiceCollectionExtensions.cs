using Contactor.Web.Features.Access;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>The demo's own health check, which answers its own machine only (the container's health check).</summary>
    public const string HealthPath = "/healthz";

    /// <summary>The demo: its settings, the visitors' copies, what starts their programs, and the proxy.</summary>
    public static IServiceCollection AddDemo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<DemoOptions>, DemoOptionsPaths>();
        services.AddSingleton<IValidateOptions<DemoOptions>, DemoOptionsValidation>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<DemoOptions>>(TrustTheTunnelOnly);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProgramStarter, DotnetProgramStarter>();
        services.TryAddSingleton<IFreePorts, LoopbackPorts>();
        services.TryAddSingleton<ICopyDatabase, AppCopyDatabase>();
        services.AddSingleton<VisitorCopies>();
        services.AddSingleton<DemoProxy>();
        services.AddHttpForwarder();
        services.AddHostedService<DemoHostedService>();
        return services;
    }

    /// <summary>
    /// The demo's requests: the scheme the tunnel says the visitor came in with, the demo's health check, and everything
    /// else to the demo's proxy: the page, the stylesheet, and the visitors' copies.
    /// </summary>
    public static WebApplication MapDemo(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.MapGet(HealthPath, (HttpContext context) => ThisMachine.Asked(context) ? Results.Text("ok") : Results.NotFound());
        var proxy = app.Services.GetRequiredService<DemoProxy>();
        app.Map("/{**path}", proxy.HandleAsync);
        return app;
    }

    // Only X-Forwarded-Proto, and only from Demo:TunnelAddress: the visitor's cookie is Secure when they came in over
    // HTTPS, and nobody else can say so. Nothing else is taken from the headers: the demo uses no client address. With
    // no address, the headers are read from nobody (with no known proxy at all, ASP.NET Core would believe anyone).
    private static void TrustTheTunnelOnly(ForwardedHeadersOptions forwarded, IOptions<DemoOptions> demo)
    {
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        forwarded.ForwardLimit = 1;
        if (DemoOptions.TryParseTunnelAddress(demo.Value.TunnelAddress, out var tunnel))
        {
            forwarded.KnownProxies.Add(tunnel);
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        }
        else
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.None;
        }
    }
}
