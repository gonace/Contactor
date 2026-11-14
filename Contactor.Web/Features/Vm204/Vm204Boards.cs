using Contactor.Boards;
using Contactor.Boards.Vm204;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Vm204;

public static class Vm204Boards
{
    /// <summary>
    /// The VM204, built by the board catalogue when it is chosen: <see cref="Vm204Client"/> behind the app's board
    /// interfaces, with its settings from <c>Vm204</c>. It has nothing of its own in the app.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Vm204Options>()
            .Bind(setup.Settings(configuration.GetSection(Vm204Options.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<Vm204Client>();
        services.AddSingleton<Vm204Board>();
        services.AddSingleton<Vm204Connection>();
        services.AddSingleton<Vm204Traffic>();
        return ActiveBoard.Build<Vm204Board, Vm204Connection, Vm204Traffic>(model, setup, services);
    }
}
