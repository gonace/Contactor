using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Schedules;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        // The events' words, which take a schedule's. AddLanguages also adds the localization; adding it twice is harmless.
        services.AddLocalization();
        services.TryAddSingleton<ScheduleText>();
        services.TryAddSingleton<EventText>();
        services.AddSingleton<EventsHandler>();
        return services;
    }
}
