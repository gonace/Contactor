using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;
using Contactor.Web.Features.Backup;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class EventRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_RemoveEventsOlderThanAYear_AndKeepTheRest()
    {
        var time = new FixedTimeProvider(Now);
        await using var database = await CreateAsync(time);
        var cutoff = new DateTime(2025, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        await AddAsync(database, cutoff.AddTicks(-1));
        var atCutoff = await AddAsync(database, cutoff);
        var newer = await AddAsync(database, cutoff.AddSeconds(1));
        var now = await AddAsync(database, Now);

        var cleanup = await database.Services.GetRequiredService<EventRetention>().RemoveOldAsync(Cancel);

        Assert.Equal(new EventCleanup(1, 1), cleanup);
        Assert.Equal([atCutoff, newer, now], await IdsAsync(database));
    }

    [Fact]
    public async Task Should_CountFromThe28thOfFebruary_OnALeapDay()
    {
        var time = new FixedTimeProvider(new DateTime(2028, 2, 29, 12, 0, 0, DateTimeKind.Utc));
        await using var database = await CreateAsync(time);
        await AddAsync(database, new DateTime(2027, 2, 28, 11, 59, 59, DateTimeKind.Utc));
        var kept = await AddAsync(database, new DateTime(2027, 2, 28, 12, 0, 0, DateTimeKind.Utc));
        var march = await AddAsync(database, new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        await database.Services.GetRequiredService<EventRetention>().RemoveOldAsync(Cancel);

        Assert.Equal([kept, march], await IdsAsync(database));
    }

    [Fact]
    public async Task Should_RemoveInBatches()
    {
        var time = new FixedTimeProvider(Now);
        await using var database = await CreateAsync(time);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var old = Now.AddYears(-2);
            db.EventLog.AddRange(Enumerable.Range(0, (2 * EventRetention.BatchSize) + 1).Select(i => Event(old.AddMinutes(i))));
            await db.SaveChangesAsync(Cancel);
        }

        var kept = await AddAsync(database, Now.AddDays(-1));

        var cleanup = await database.Services.GetRequiredService<EventRetention>().RemoveOldAsync(Cancel);

        Assert.Equal(new EventCleanup((2 * EventRetention.BatchSize) + 1, 3), cleanup);
        Assert.Equal([kept], await IdsAsync(database));
    }

    [Fact]
    public async Task Should_KeepTheNewestEventsTheAppReadsBack_WhenOld()
    {
        var time = new FixedTimeProvider(Now);
        await using var database = await CreateAsync(time);
        var old = Now.AddYears(-2);
        await AddAsync(database, old, EventActions.InputChanged, inputId: 1);
        var input = await AddAsync(database, old.AddHours(1), EventActions.InputChanged, inputId: 1);
        var off = await AddAsync(database, old.AddHours(2), EventActions.Off, relayId: 3);
        await AddAsync(database, old.AddHours(3), EventActions.On, relayId: 3);
        var pulse = await AddAsync(database, old.AddHours(4), EventActions.Pulse, relayId: 4);
        await AddAsync(database, old.AddHours(5), EventActions.UpdateCurrent);
        var update = await AddAsync(database, old.AddHours(6), EventActions.UpdateDone);
        await AddAsync(database, old.AddHours(7), EventActions.Rename, relayId: 3);

        var cleanup = await database.Services.GetRequiredService<EventRetention>().RemoveOldAsync(Cancel);

        Assert.Equal(4, cleanup.Removed);
        Assert.Equal([input, off, pulse, update], await IdsAsync(database));
    }

    [Fact]
    public async Task Should_RemoveOldEvents_RightAfterTheNightlyBackup()
    {
        // 00:00 in Stockholm: the backup is at 02:30.
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 14, 23, 0, 0, TimeSpan.Zero));
        await using var database = await CreateAsync(time);
        var old = await AddAsync(database, time.GetUtcNow().UtcDateTime.AddYears(-1).AddDays(-1));
        var backup = database.Services.GetRequiredService<BackupService>();
        await backup.StartAsync(Cancel);

        await RunNightAsync(time, backup);

        Assert.Empty(await IdsAsync(database));
        Assert.Equal([old], await IdsInAsync(Path.Combine(database.Folder, "data", "backups", "contactor-2026-11-15.db")));
        await backup.StopAsync(Cancel);
    }

    [Fact]
    public async Task Should_TryAgainTheNextNight_AfterAFailedCleanup()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 14, 23, 0, 0, TimeSpan.Zero));
        await using var database = await CreateAsync(time);
        var old = await AddAsync(database, time.GetUtcNow().UtcDateTime.AddYears(-2));
        var backup = database.Services.GetRequiredService<BackupService>();
        await backup.StartAsync(Cancel);

        // With the table gone the copy is made, but the cleanup fails.
        await ExecuteAsync(database, "ALTER TABLE EventLog RENAME TO EventLogAway;");
        await RunNightAsync(time, backup);
        await ExecuteAsync(database, "ALTER TABLE EventLogAway RENAME TO EventLog;");
        Assert.Equal([old], await IdsAsync(database));
        Assert.True(File.Exists(Path.Combine(database.Folder, "data", "backups", "contactor-2026-11-15.db")));

        await RunNightAsync(time, backup);

        Assert.Empty(await IdsAsync(database));
        await backup.StopAsync(Cancel);
    }

    [Fact]
    public async Task Should_StillRemoveOldEvents_AfterAFailedBackup()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 14, 23, 0, 0, TimeSpan.Zero));
        var folder = Path.Combine(Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        await File.WriteAllTextAsync(folder, "a file, not a folder", Cancel);
        try
        {
            await using var database = await CreateAsync(time, folder);
            await AddAsync(database, time.GetUtcNow().UtcDateTime.AddYears(-2));
            var backup = database.Services.GetRequiredService<BackupService>();
            await backup.StartAsync(Cancel);

            await RunNightAsync(time, backup);

            Assert.Empty(await IdsAsync(database));
            await backup.StopAsync(Cancel);
        }
        finally
        {
            File.Delete(folder);
        }
    }

    // Moves the clock to the next backup and waits until the night is done: its wait for the next night has started.
    private static async Task RunNightAsync(ManualTimeProvider time, BackupService backup)
    {
        await BoardTestRig.WaitUntilAsync(() => time.PendingTimers == 1);
        time.Advance(backup.UntilNext());
        await BoardTestRig.WaitUntilAsync(() => time.PendingTimers == 1);
    }

    private static async Task<TestDatabase> CreateAsync(TimeProvider time, string? backupFolder = null)
    {
        var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton(time);
                services.AddBackup(configuration);
            },
            backupFolder is null ? null : new Dictionary<string, string?> { ["Backup:Folder"] = backupFolder });
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        return database;
    }

    private static EventLogEntry Event(DateTime timestampUtc, string action = EventActions.On, int? relayId = null, int? inputId = null)
    {
        return new EventLogEntry { TimestampUtc = timestampUtc, RelayId = relayId, InputId = inputId, Actor = EventActors.System, Action = action };
    }

    private static async Task<int> AddAsync(TestDatabase database, DateTime timestampUtc, string action = EventActions.On, int? relayId = null, int? inputId = null)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = Event(timestampUtc, action, relayId, inputId);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(Cancel);
        return entry.Id;
    }

    private static async Task<List<int>> IdsAsync(TestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(Cancel);
    }

    private static async Task<List<int>> IdsInAsync(string path)
    {
        await using var copy = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync(Cancel);
        await using var command = copy.CreateCommand();
        command.CommandText = "SELECT Id FROM EventLog ORDER BY Id;";
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new SqliteConnection(database.ConnectionString + ";Pooling=False");
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancel);
    }
}
