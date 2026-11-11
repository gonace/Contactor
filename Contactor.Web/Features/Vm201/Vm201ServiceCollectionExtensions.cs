using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Vm201;

public static class Vm201ServiceCollectionExtensions
{
    /// <summary>
    /// The one connection to the board: <see cref="Vm201Client"/> as a singleton, kept open by
    /// <see cref="Vm201HostedService"/>, and the board as an <see cref="IRelayBoard"/> through <see cref="Vm201Board"/>.
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

        // The rest of the app only knows the board as an IRelayBoard.
        services.AddSingleton<Vm201Board>();
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<Vm201Board>());
        services.AddSingleton<BoardAddressHandler>();
        services.AddSingleton<BoardTrace>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<Vm201HostedService>();
        return services;
    }
}
