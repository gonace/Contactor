using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Schedules;

public static class SchedulesServiceCollectionExtensions
{
    public static IServiceCollection AddSchedules(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<SchedulesHandler>();
        services.AddSingleton<ScheduleOutlook>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<SchedulerService>());
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        return services;
    }
}
