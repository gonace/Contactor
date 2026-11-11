using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Vm201;

public static class Vm201ServiceCollectionExtensions
{
    /// <summary>
    /// The VM201, registered by the board catalogue when <c>Board:Type</c> is <c>vm201</c>: <see cref="Vm201Client"/>
    /// as a singleton behind the app's board interfaces, and <see cref="Vm201TimersService"/> for its own timers.
    /// </summary>
    public static IServiceCollection AddVm201(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<Vm201Options>()
            .Bind(configuration.GetSection(Vm201Options.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Vm201:Host is required.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Vm201Client>();

        // The rest of the app only knows the board through these.
        services.AddSingleton<Vm201Board>();
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<Vm201Board>());
        services.AddSingleton<IBoardConnection, Vm201Connection>();
        services.AddSingleton<IBoardTraffic, Vm201Traffic>();
        services.AddHostedService<Vm201TimersService>();
        return services;
    }
}
