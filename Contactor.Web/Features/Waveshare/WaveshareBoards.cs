using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Waveshare;

public static class WaveshareBoards
{
    /// <summary>
    /// A Waveshare Modbus PoE ETH relay, built by the board catalogue when one of its models is chosen:
    /// <see cref="WaveshareClient"/> behind the app's board interfaces (it reads the model), with its settings from
    /// <c>Waveshare</c>, and <see cref="WaveshareRelayModesService"/>, which logs when the client set the relays to
    /// normal mode.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<WaveshareOptions>()
            .Bind(configuration.GetSection(WaveshareOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<WaveshareClient>();
        services.AddSingleton<WaveshareBoard>();
        services.AddSingleton<WaveshareConnection>();
        services.AddSingleton<WaveshareTraffic>();
        return ActiveBoard.Build<WaveshareBoard, WaveshareConnection, WaveshareTraffic>(
            model,
            services,
            own => [ActivatorUtilities.CreateInstance<WaveshareRelayModesService>(app, own.GetRequiredService<WaveshareClient>())]);
    }
}
