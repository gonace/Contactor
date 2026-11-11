using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Contactor.ServiceDefaults;

namespace Contactor.Simulator;

/// <summary>
/// A fake VM201 relay board on a local TCP port, for development. Started by Contactor.AppHost.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.Services.AddOptions<SimulatorOptions>()
            .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
            .Validate(o => o.Port is >= 1 and <= 65535, "Simulator:Port must be 1–65535.")
            .ValidateOnStart();
        builder.Services.AddHostedService<SimulatorService>();
        builder.Build().Run();
    }
}
