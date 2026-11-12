using Contactor.Boards;
using Contactor.Boards.Vm204;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Vm204;

public static class Vm204ServiceCollectionExtensions
{
    /// <summary>
    /// The VM204, registered by the board catalogue when <c>Board:Type</c> is <c>vm204</c>: <see cref="Vm204Client"/>
    /// as a singleton behind the app's board interfaces. It has nothing of its own in the app.
    /// </summary>
    public static IServiceCollection AddVm204(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<Vm204Options>()
            .Bind(configuration.GetSection(Vm204Options.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Vm204Client>();

        // The rest of the app only knows the board through these.
        services.AddSingleton<Vm204Board>();
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<Vm204Board>());
        services.AddSingleton<IBoardConnection, Vm204Connection>();
        services.AddSingleton<IBoardTraffic, Vm204Traffic>();
        return services;
    }
}
