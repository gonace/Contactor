using Microsoft.Extensions.DependencyInjection.Extensions;
using Contactor.Vm201;

namespace Contactor.Web.Features.Vm201;

public static class Vm201ServiceCollectionExtensions
{
    /// <summary>
    /// The one connection to the board: <see cref="Vm201Client"/> as a singleton, kept open by
    /// <see cref="Vm201HostedService"/>.
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
        services.AddSingleton<BoardAddressHandler>();
        services.AddSingleton<BoardTrace>();
        services.AddSingleton<BoardTrafficSetting>();
        services.AddHostedService<Vm201HostedService>();
        return services;
    }
}
