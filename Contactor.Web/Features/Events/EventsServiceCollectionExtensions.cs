namespace Contactor.Web.Features.Events;

public static class EventsServiceCollectionExtensions
{
    /// <summary>The event log: what the app expects from the board, what happens there by itself, and the Händelser screen.</summary>
    public static IServiceCollection AddEvents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<ExpectedChanges>();
        services.AddHostedService<BoardEventRecorder>();
        services.AddSingleton<EventsHandler>();
        return services;
    }
}
