using Contactor.ServiceDefaults;
using Contactor.Web.Components;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Backup;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Features.Setup;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Features.Updates;
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

        if (args is [SignInReset.Command, ..])
        {
            return SignInReset.Run(args);
        }

        if (args is [SetupStatus.Command, ..])
        {
            return SetupStatus.Run(args);
        }

        if (args is [UpdateBackup.Command, ..])
        {
            return UpdateBackup.Run(args);
        }

        if (AppCommands.RefuseUnknown(args, Console.Error) is { } exitCode)
        {
            return exitCode;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ListenForTailscaleServe(builder.Configuration);
        builder.AddServiceDefaults();
        builder.Services.AddLocalClock(builder.Configuration);
        builder.Services.AddPersistence(builder.Configuration);
        builder.Services.AddBoards(builder.Configuration);
        builder.Services.AddSignIn(builder.Configuration);
        builder.Services.AddCloudflare(builder.Configuration);
        builder.Services.AddTailscale(builder.Configuration);
        builder.Services.AddRelays(builder.Configuration);
        builder.Services.AddGroups(builder.Configuration);
        builder.Services.AddEvents(builder.Configuration);
        builder.Services.AddInputs(builder.Configuration);
        builder.Services.AddSchedules(builder.Configuration);
        builder.Services.AddBackup(builder.Configuration);
        builder.Services.AddUpdates(builder.Configuration);
        builder.Services.AddSetup(builder.Configuration);
        builder.Services.AddAccess(builder.Configuration);
        builder.Services.AddLanguages(builder.Configuration);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        LogBuild(app.Logger, AppBuild.Current.Version, AppBuild.Current.Commit ?? "unknown");
        app.CheckForwardedHeadersOff();
        app.Services.InitializeDatabaseAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.GetRequiredService<SignInChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.CheckSignInFitsWayIn();
        app.Services.GetRequiredService<AppLanguage>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.ApplyCloudflareSettingsAsync(CancellationToken.None).GetAwaiter().GetResult();
        app.Services.ApplyTailscaleSettingsAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (app.Services.GetRequiredService<SignInChoice>().Current is null)
        {
            app.Services.GetRequiredService<SetupCode>().Renew();
        }

        // The app's one language, for every request and every page's live connection; first, so even a refusal has it.
        app.UseRequestLocalization();
        app.UseSignIn();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapDefaultEndpoints();
        app.MapHealthGroup();
        app.MapSetupGroup();
        app.MapBackupGroup();
        app.MapUpdatesGroup();
        app.MapRelaysGroup();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Contactor {Version} (commit {Commit})")]
    private static partial void LogBuild(ILogger logger, string version, string commit);
}
