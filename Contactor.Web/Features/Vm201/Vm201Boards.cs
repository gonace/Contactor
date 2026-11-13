using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Vm201;

public static class Vm201Boards
{
    /// <summary>
    /// The VM201, built by the board catalogue when it is chosen: <see cref="Vm201Client"/> behind the app's board
    /// interfaces, with its settings from <c>Vm201</c>, and <see cref="Vm201TimersService"/> for its own timers.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Vm201Options>()
            .Bind(configuration.GetSection(Vm201Options.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<Vm201Client>();
        services.AddSingleton<Vm201Board>();
        services.AddSingleton<Vm201Connection>();
        services.AddSingleton<Vm201Traffic>();
        return ActiveBoard.Build<Vm201Board, Vm201Connection, Vm201Traffic>(
            model,
            services,
            own =>
            [
                ActivatorUtilities.CreateInstance<Vm201TimersService>(
                    app,
                    own.GetRequiredService<Vm201Client>(),
                    own.GetRequiredService<IOptions<Vm201Options>>()),
            ]);
    }
}
