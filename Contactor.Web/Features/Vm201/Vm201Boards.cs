using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Vm201;

public static class Vm201Boards
{
    /// <summary>
    /// The VM201, built by the board catalogue when it is chosen: <see cref="Vm201Client"/> behind the app's board
    /// interfaces, with its settings from <c>Vm201</c>. Its own timers are the board's own rules, changed only on a press.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Vm201Options>()
            .Bind(setup.Settings(configuration.GetSection(Vm201Options.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<Vm201Client>();
        services.AddSingleton<Vm201Board>();
        services.AddSingleton<Vm201Connection>();
        services.AddSingleton<Vm201Traffic>();
        return ActiveBoard.Build<Vm201Board, Vm201Connection, Vm201Traffic>(model, setup, services);
    }
}
