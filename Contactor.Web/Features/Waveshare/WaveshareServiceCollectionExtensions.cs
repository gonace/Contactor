using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Waveshare;

public static class WaveshareServiceCollectionExtensions
{
    /// <summary>
    /// A Waveshare Modbus PoE ETH relay, registered by the board catalogue when <c>Board:Type</c> names one of its
    /// models: <see cref="WaveshareClient"/> as a singleton behind the app's board interfaces (it reads the
    /// <see cref="BoardModel"/> the catalogue registers), and <see cref="WaveshareRelayModesService"/>, which logs when
    /// the client set the relays to normal mode.
    /// </summary>
    public static IServiceCollection AddWaveshare(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<WaveshareOptions>()
            .Bind(configuration.GetSection(WaveshareOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WaveshareClient>();

        // The rest of the app only knows the board through these.
        services.AddSingleton<WaveshareBoard>();
        services.AddSingleton<IRelayBoard>(provider => provider.GetRequiredService<WaveshareBoard>());
        services.AddSingleton<IBoardConnection, WaveshareConnection>();
        services.AddSingleton<IBoardTraffic, WaveshareTraffic>();
        services.AddHostedService<WaveshareRelayModesService>();
        return services;
    }
}
