using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Waveshare;

public static class WaveshareBoards
{
    /// <summary>
    /// A Waveshare Modbus PoE ETH relay, built by the board catalogue when one of its models is chosen:
    /// <see cref="WaveshareClient"/> behind the app's board interfaces (it reads the model), with its settings from
    /// <c>Waveshare</c>. Its relays' control modes come in its state as its own rules, which the app's own-rules service
    /// remembers and logs; nothing of its own runs beside its connection.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<WaveshareOptions>()
            .Bind(setup.Settings(configuration.GetSection(WaveshareOptions.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<WaveshareClient>();
        services.AddSingleton<WaveshareBoard>();
        services.AddSingleton<WaveshareConnection>();
        services.AddSingleton<WaveshareTraffic>();
        return ActiveBoard.Build<WaveshareBoard, WaveshareConnection, WaveshareTraffic>(model, setup, services);
    }
}
