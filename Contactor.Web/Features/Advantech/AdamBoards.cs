using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Advantech;

public static class AdamBoards
{
    /// <summary>
    /// An Advantech ADAM module, built by the board catalogue when it is chosen: <see cref="AdamClient"/> behind the
    /// app's board interfaces, with its settings from <c>Advantech</c>. It has nothing of its own in the app.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<AdamOptions>()
            .Bind(setup.Settings(configuration.GetSection(AdamOptions.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<AdamClient>();
        services.AddSingleton<AdamBoard>();
        services.AddSingleton<AdamConnection>();
        services.AddSingleton<AdamTraffic>();
        return ActiveBoard.Build<AdamBoard, AdamConnection, AdamTraffic>(model, setup, services);
    }
}
