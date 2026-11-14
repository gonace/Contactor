using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Events;

public static class EventsServiceCollectionExtensions
{
    /// <summary>The event log: what happens at each board by itself, and the Events screen.</summary>
    public static IServiceCollection AddEvents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<BoardEventRecorder>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<BoardEventRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<BoardEventRecorder>());
        services.AddSingleton<EventsHandler>();
        return services;
    }
}
