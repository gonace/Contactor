using Contactor.Boards;
using Contactor.Boards.SmartDen;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.SmartDen;

public static class SmartDenBoards
{
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
