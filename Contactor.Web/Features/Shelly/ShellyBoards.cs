using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Shelly;

public static class ShellyBoards
{
    /// <summary>
    /// A Shelly Gen2+ device with 1 to 4 relays, built by the board catalogue when one is chosen:
    /// <see cref="ShellyClient"/> behind the app's board interfaces, with its settings from <c>Shelly</c>. It has nothing
    /// of its own in the app: its own schedules and auto-on show and turn off through the board's own rules, as any
    /// board's; its auto-off is left as it is.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<ShellyOptions>()
            .Bind(setup.Settings(configuration.GetSection(ShellyOptions.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<ShellyClient>();
        services.AddSingleton<ShellyBoard>();
        services.AddSingleton<ShellyConnection>();
        services.AddSingleton<ShellyTraffic>();
        return ActiveBoard.Build<ShellyBoard, ShellyConnection, ShellyTraffic>(model, setup, services);
    }
}
