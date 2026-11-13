using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Devantech;

public static class DevantechBoards
{
    /// <summary>
    /// A Devantech ETH relay board, built by the board catalogue when one of its models is chosen:
    /// <see cref="DevantechClient"/> behind the app's board interfaces (it reads the model), with its settings from
    /// <c>Devantech</c>. It has nothing of its own in the app.
    /// </summary>
    public static ActiveBoard Create(BoardModel model, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<DevantechOptions>()
            .Bind(configuration.GetSection(DevantechOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<DevantechClient>();
        services.AddSingleton<DevantechBoard>();
        services.AddSingleton<DevantechConnection>();
        services.AddSingleton<DevantechTraffic>();
        return ActiveBoard.Build<DevantechBoard, DevantechConnection, DevantechTraffic>(model, services);
    }
}
