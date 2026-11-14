using Contactor.Boards;
using Contactor.Boards.Vm208;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Vm208;

public static class Vm208Boards
{
    /// <summary>
    /// The VM208, built by the board catalogue when it is chosen: <see cref="Vm208Client"/> behind the app's board
    /// interfaces, with its settings from <c>Vm208</c>. It has nothing of its own in the app yet; the board's own
    /// schedules, pulses and timers are left as they are.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Vm208Options>()
            .Bind(setup.Settings(configuration.GetSection(Vm208Options.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<Vm208Client>();
        services.AddSingleton<Vm208Board>();
        services.AddSingleton<Vm208Connection>();
        services.AddSingleton<Vm208Traffic>();
        return ActiveBoard.Build<Vm208Board, Vm208Connection, Vm208Traffic>(model, setup, services);
    }
}
