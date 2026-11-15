using Contactor.Web.Features.Updates;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Backup;

/// <summary>
/// <c>Contactor.Web update-backup</c>, run by the update helper on the host just before it restarts the app on a new
/// version (<c>docker exec contactor dotnet /app/Contactor.Web.dll update-backup</c>): copies the database with SQLite's
/// <c>VACUUM INTO</c>, a consistent copy while the app keeps running, to <c>updates/before-update.db</c>. The helper
/// puts it back when the new version doesn't come up, since that version may have changed the database. It writes the
/// copy's path, and never starts the app. While a group is starting or running on, the restart would cut it off: it
/// copies nothing and exits with <see cref="GroupsBusyExitCode"/>, and the helper asks again a little later.
/// </summary>
public static class UpdateBackup
{
    public const string Command = "update-backup";

    /// <summary>A group is starting or running on (install/contactor-updater.sh's GROUPS_BUSY).</summary>
    public const int GroupsBusyExitCode = 3;

    public static int Run(string[] args)
    {
        return Run(args, Console.Out, Console.Error);
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var builder = WebApplication.CreateBuilder(args.Where(a => a != Command).ToArray());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(builder.Configuration);
        services.AddOptions<UpdateOptions>().Bind(builder.Configuration.GetSection(UpdateOptions.SectionName));
        services.AddSingleton<UpdateFolder>();
        using var provider = services.BuildServiceProvider();
        var location = provider.GetRequiredService<DatabaseLocation>();
        if (!File.Exists(location.Path))
        {
            error.WriteLine($"update-backup: there is no database at {location.Path}.");
            return 1;
        }

        var folder = provider.GetRequiredService<UpdateFolder>();
        var partial = folder.BackupPath + ".partial";
        try
        {
            Directory.CreateDirectory(folder.Path);
            File.Delete(partial);
            using var db = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContext();
            if (db.GroupRuntimes.Any(r => r.Phase != GroupPhases.Idle))
            {
                error.WriteLine("update-backup: a group is starting or running on; the update waits for it.");
                return GroupsBusyExitCode;
            }

            // Raw SQL: EF Core has no VACUUM. The path goes in as a parameter, never into the SQL text.
            db.Database.ExecuteSqlRaw("VACUUM INTO {0};", [partial]);
            File.Move(partial, folder.BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"update-backup: the database couldn't be copied ({ex.Message}).");
            return 1;
        }

        output.WriteLine(folder.BackupPath);
        return 0;
    }
}
