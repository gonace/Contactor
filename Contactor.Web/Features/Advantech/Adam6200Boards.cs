using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Advantech;

public static class Adam6200Boards
{
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<Adam6200Options>()
            .Bind(setup.Settings(configuration.GetSection(Adam6200Options.SectionName)))
            .Validate(options => options.Port is > 0 and <= 65535 && options.PollSeconds > 0);
        services.AddSingleton<Adam6200Client>();
        services.AddSingleton<Adam6200Board>();
        services.AddSingleton<Adam6200Connection>();
        services.AddSingleton<Adam6200Traffic>();
        return ActiveBoard.Build<Adam6200Board, Adam6200Connection, Adam6200Traffic>(model, setup, services);
    }
}
