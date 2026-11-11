using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Backup;

/// <summary>The <c>Backup</c> configuration section.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Where the copies go; empty for a <c>backups</c> folder next to the database.</summary>
    public string Folder { get; set; } = "";

    /// <summary>How many daily copies are kept.</summary>
    public int Keep { get; set; } = 14;

    /// <summary>The local time of day the copy is made, before the nightly update between 03:00 and 04:00.</summary>
    public TimeOnly At { get; set; } = new(2, 30);
}

/// <summary>
/// Copies the database once a night with SQLite's <c>VACUUM INTO</c>, which gives a consistent copy while the app
/// keeps writing (a file copy of a WAL database can catch it half-written), and keeps the latest copies.
/// </summary>
public sealed partial class BackupService : BackgroundService
{
    public const string FilePrefix = "contactor-";
    public const string FileExtension = ".db";

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BackupOptions _options;
    private readonly LocalClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IOptions<BackupOptions> options,
        LocalClock clock,
        TimeProvider timeProvider,
        ILogger<BackupService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbFactory = dbFactory;
        _options = options.Value;
        _clock = clock;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Makes today's copy (replacing an earlier one from today) and removes the oldest beyond the limit.</summary>
    /// <returns>The copy's path.</returns>
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = Folder(db);
        Directory.CreateDirectory(folder);
        var today = _clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{FilePrefix}{today}{FileExtension}");
        var partial = path + ".partial";
        File.Delete(partial);

        // Raw SQL: EF Core has no VACUUM. The path goes in as a parameter, never into the SQL text.
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0};", [partial], cancellationToken);
        File.Move(partial, path, overwrite: true);

        foreach (var old in Directory.GetFiles(folder, $"{FilePrefix}*{FileExtension}").OrderDescending(StringComparer.Ordinal).Skip(_options.Keep))
        {
            File.Delete(old);
        }

        LogBackedUp(path);
        return path;
    }

    /// <summary>The newest copy, or null when there is none yet.</summary>
    public async Task<FileInfo?> LatestAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = Folder(db);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var newest = Directory.GetFiles(folder, $"{FilePrefix}*{FileExtension}").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        return newest is null ? null : new FileInfo(newest);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNext(), _timeProvider, stoppingToken);
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed backup must not stop the next one; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
    }

    // Until the next time of day in local time; at least a minute, so a run never repeats at once. Worked out in UTC,
    // so a night with a clock change waits the real time (local wall times differ by an hour then).
    public TimeSpan UntilNext()
    {
        var nowUtc = _clock.UtcNow;
        var today = DateOnly.FromDateTime(_clock.Now);
        var nextUtc = ScheduleCalendar.ToUtc(today, _options.At, _clock.Zone);
        if (nextUtc <= nowUtc.AddMinutes(1))
        {
            nextUtc = ScheduleCalendar.ToUtc(today.AddDays(1), _options.At, _clock.Zone);
        }

        return nextUtc - nowUtc;
    }

    private string Folder(ContactorDbContext db)
    {
        if (!string.IsNullOrWhiteSpace(_options.Folder))
        {
            return _options.Folder;
        }

        var database = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database)) ?? ".", "backups");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the database to {Path}")]
    private partial void LogBackedUp(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not back up the database")]
    private partial void LogFailed(Exception exception);
}

public static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddBackup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName))
            .Validate(o => o.Keep is >= 1 and <= 365, "Backup:Keep must be 1–365.")
            .ValidateOnStart();
        services.AddSingleton<BackupService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
        return services;
    }
}
