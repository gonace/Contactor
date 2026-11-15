using Contactor.Boards;
using Contactor.Boards.DAEnetIP2v2;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.DAEnetIP2v2;

public static class DAEnetIP2v2Boards
{
    public static ActiveBoard Create(BoardModel model, BoardSetup setup, IServiceProvider app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(configuration);
        var services = ActiveBoard.NewServices(model, app);
        services.AddOptions<DAEnetIP2v2Options>()
            .Bind(setup.Settings(configuration.GetSection(DAEnetIP2v2Options.SectionName)))
            .ValidateDataAnnotations();
        services.AddSingleton<DAEnetIP2v2Client>();
        services.AddSingleton<DAEnetIP2v2Board>();
        services.AddSingleton<DAEnetIP2v2Connection>();
        services.AddSingleton<DAEnetIP2v2Traffic>();
        return ActiveBoard.Build<DAEnetIP2v2Board, DAEnetIP2v2Connection, DAEnetIP2v2Traffic>(model, setup, services);
    }
}
