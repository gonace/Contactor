using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Groups;

public static class GroupsServiceCollectionExtensions
{
    /// <summary>Relay groups: <see cref="GroupsHandler"/> to set them up, <see cref="GroupSequencer"/> to run them.</summary>
    public static IServiceCollection AddGroups(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // The texts of the handler and the text services; registered with the languages too, and once is enough.
        services.AddLocalization();
        services.AddSingleton<GroupsHandler>();
        services.AddSingleton<GroupStatusText>();
        services.AddSingleton<GroupPlanText>();
        services.AddSingleton<GroupSequencer>();
        services.AddSingleton<IBoardWatcher>(sp => sp.GetRequiredService<GroupSequencer>());
        services.AddHostedService(sp => sp.GetRequiredService<GroupSequencer>());
        return services;
    }
}
