using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Advantech;

public static class Adam6000Boards
{
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Adam6000Options>()
            .Bind(setup.Settings(configuration.GetSection(Adam6000Options.SectionName)))
            .Validate(options => options.Port is > 0 and <= 65535 && options.PollSeconds > 0);
        services.AddSingleton<Adam6000Client>();
        services.AddSingleton<Adam6000Board>();
        services.AddSingleton<Adam6000Connection>();
        services.AddSingleton<Adam6000Traffic>();
        return ActiveBoard.Build<Adam6000Board, Adam6000Connection, Adam6000Traffic>(model, setup, services);
    }
}
