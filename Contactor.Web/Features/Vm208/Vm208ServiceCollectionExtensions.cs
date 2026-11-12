using Contactor.Boards;
using Contactor.Boards.Vm208;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Vm208;

public static class Vm208ServiceCollectionExtensions
{
    /// <summary>
    /// The VM208, registered by the board catalogue when <c>Board:Type</c> is <c>vm208</c>: <see cref="Vm208Client"/>
    /// as a singleton behind the app's board interfaces. It has nothing of its own in the app yet; the board's own
    /// schedules, pulses and timers are left as they are.
    /// </summary>
    public static IServiceCollection AddVm208(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<Vm208Options>()
            .Bind(configuration.GetSection(Vm208Options.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Vm208Client>();

        // The rest of the app only knows the board through these.
        services.AddSingleton<Vm208Board>();
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<Vm208Board>());
        services.AddSingleton<IBoardConnection, Vm208Connection>();
        services.AddSingleton<IBoardTraffic, Vm208Traffic>();
        return services;
    }
}
