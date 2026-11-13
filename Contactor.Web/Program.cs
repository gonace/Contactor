using Contactor.ServiceDefaults;
using Contactor.Web.Components;
using Contactor.Web.Features.Backup;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Features.Setup;
using Contactor.Web.Features.Tunnel;
using Contactor.Web.Persistence;

namespace Contactor.Web;

public sealed partial class Program
{
    public static int Main(string[] args)
    {
        // The container's health check: the image has no shell or curl, so the app asks itself.
        if (args is [HealthProbe.Command, ..])
        {
            return HealthProbe.Run(args.Length > 1 ? args[1] : HealthProbe.DefaultUrl);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.Services.AddLocalClock(builder.Configuration);
        builder.Services.AddPersistence(builder.Configuration);
        builder.Services.AddBoards(builder.Configuration);
        builder.Services.AddCloudflareAccess(builder.Configuration);
        builder.Services.AddRelays(builder.Configuration);
        builder.Services.AddGroups(builder.Configuration);
        builder.Services.AddEvents(builder.Configuration);
        builder.Services.AddInputs(builder.Configuration);
        builder.Services.AddSchedules(builder.Configuration);
        builder.Services.AddBackup(builder.Configuration);
        builder.Services.AddSetup(builder.Configuration);
        builder.Services.AddTunnel(builder.Configuration);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        LogBuild(app.Logger, AppBuild.Current.Version, AppBuild.Current.Commit ?? "unknown");
        if (app.Services.GetRequiredService<DatabaseLocation>() is { IsEarlierFolder: true } location)
        {
            LogEarlierFolder(app.Logger, location.Path);
        }
        app.Services.InitializeDatabaseAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();

        app.UseCloudflareAccess();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapDefaultEndpoints();
        app.MapHealthGroup();
        app.MapBackupGroup();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Using the database at {Path}, where earlier images kept it: run install.sh again so compose.yml maps it to /data")]
    private static partial void LogEarlierFolder(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Contactor {Version} (commit {Commit})")]
    private static partial void LogBuild(ILogger logger, string version, string commit);
}
