using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Denkovi;

public static class DaenetIp2Boards
{
    /// <summary>
    /// A Denkovi DAEnetIP2 v2 kit, built by the board catalogue when it is chosen: <see cref="DaenetIp2Client"/> behind
    /// the app's board interfaces, with its settings from <c>DaenetIp2</c>. It has nothing of its own in the app.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<DaenetIp2Options>()
            .Bind(setup.Settings(configuration.GetSection(DaenetIp2Options.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<DaenetIp2Client>();
        services.AddSingleton<DaenetIp2Board>();
        services.AddSingleton<DaenetIp2Connection>();
        services.AddSingleton<DaenetIp2Traffic>();
        return ActiveBoard.Build<DaenetIp2Board, DaenetIp2Connection, DaenetIp2Traffic>(model, setup, services);
    }
}
