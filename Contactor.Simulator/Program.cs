using Contactor.Boards;
using Contactor.ServiceDefaults;
using Contactor.Simulator.Components;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Simulator;

/// <summary>
/// Fake relay boards for development, each on its own local TCP port, with a page to see and drive them. Started by
/// Contactor.AppHost.
/// </summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.Services.AddOptions<SimulatorOptions>()
            .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
            .Validate(o => o.MaxBoards is null or >= 1, "Simulator:MaxBoards is 1 or more, or not set for no limit.")
            .Validate(o => o.Ports.Length == 0 || PortRange.TryParse(o.Ports, out _), "Simulator:Ports is a range of ports, e.g. 10000-10020, or empty for any port.")
            .ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SimulatedBoards>();
        builder.Services.AddHostedService<SimulatorHostedService>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapDefaultEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
    }
}
