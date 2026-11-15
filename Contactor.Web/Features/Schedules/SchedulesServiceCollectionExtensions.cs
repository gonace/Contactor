using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Schedules;

public static class SchedulesServiceCollectionExtensions
{
    public static IServiceCollection AddSchedules(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // The texts of the handler and the schedules' words; registered with the languages too, and once is enough.
        services.AddLocalization();
        services.TryAddSingleton<ScheduleText>();
        services.AddSingleton<SchedulesHandler>();
        services.AddSingleton<ScheduleOutlook>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<SchedulerService>());
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        return services;
    }
}
