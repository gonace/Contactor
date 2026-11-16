using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Denkovi;

public static class SmartDenBoards
{
    /// <summary>
    /// A Denkovi smartDEN IP-16R-MT, built by the board catalogue when it is chosen: <see cref="SmartDenClient"/>
    /// behind the app's board interfaces, with its settings from <c>SmartDen</c>. It has nothing of its own in the app.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<SmartDenOptions>()
            .Bind(setup.Settings(configuration.GetSection(SmartDenOptions.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<SmartDenClient>();
        services.AddSingleton<SmartDenBoard>();
        services.AddSingleton<SmartDenConnection>();
        services.AddSingleton<SmartDenTraffic>();
        return ActiveBoard.Build<SmartDenBoard, SmartDenConnection, SmartDenTraffic>(model, setup, services);
    }
}
