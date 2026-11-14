using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>The demo: its settings, the visitors' copies, what starts their programs, and the proxy.</summary>
    public static IServiceCollection AddDemo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<DemoOptions>, DemoOptionsPaths>();
        services.AddSingleton<IValidateOptions<DemoOptions>, DemoOptionsValidation>();
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

    /// <summary>Every request goes to the demo's proxy: the page, the stylesheet, and the visitors' copies.</summary>
    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder endpoints)
    {
        var proxy = endpoints.ServiceProvider.GetRequiredService<DemoProxy>();
        endpoints.Map("/{**path}", proxy.HandleAsync);
        return endpoints;
    }
}
