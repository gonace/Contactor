using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;
using Contactor.Web.Features.Backup;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class BackupServiceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_WriteAReadableCopyNextToTheDatabase()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero));
        await using var database = Create(time);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        var path = await database.Services.GetRequiredService<BackupService>().RunAsync(Cancel);

        Assert.Equal(Path.Combine(database.Folder, "data", "backups", "contactor-2026-11-04.db"), path);
        await using var copy = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await copy.OpenAsync(Cancel);
        await using var command = copy.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Relay;";
        Assert.Equal(8L, await command.ExecuteScalarAsync(Cancel));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task Should_ReplaceTodaysCopyAndKeepOnlyTheLatest()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.Zero));
        await using var database = Create(time, keep: 3);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        var backup = database.Services.GetRequiredService<BackupService>();

        for (var day = 0; day < 5; day++)
        {
            await backup.RunAsync(Cancel);
            await backup.RunAsync(Cancel);
            time.Now = time.Now.AddDays(1);
        }

        var files = Directory.GetFiles(Path.Combine(database.Folder, "data", "backups")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["contactor-2026-11-03.db", "contactor-2026-11-04.db", "contactor-2026-11-05.db"], files);
    }

    [Fact]
    public async Task Should_UseTheLocalDate()
    {
        // 23:30 UTC on 3 November is 00:30 on 4 November in Stockholm.
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero));
        await using var database = Create(time);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        var path = await database.Services.GetRequiredService<BackupService>().RunAsync(Cancel);

        Assert.EndsWith("contactor-2026-11-04.db", path, StringComparison.Ordinal);
    }

    private static TestDatabase Create(FixedTimeProvider time, int keep = 14)
    {
        return TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(time);
                services.AddBackup(configuration);
            },
            new Dictionary<string, string?> { ["Backup:Keep"] = keep.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    [Theory]
    [InlineData("2026-11-03T23:00:00Z", 2.5)] // 00:00 local: 02:30 is two and a half hours away.
    [InlineData("2026-11-04T01:30:00Z", 24.0)] // 02:30 local: the next copy is tomorrow's.
    [InlineData("2026-03-28T23:00:00Z", 2.0)] // 00:00 on the spring change: 02:30 doesn't exist and runs at 03:00, two real hours away.
    [InlineData("2026-10-24T22:00:00Z", 2.5)] // 00:00 on the autumn change: the first 02:30 comes before the doubled hour.
    [InlineData("2026-10-25T00:30:00Z", 25.0)] // The first 02:30 that night: the next copy is tomorrow's, after the extra hour.
    public async Task Should_WaitTheRealTimeUntilTheNextCopy(string nowUtc, double hours)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse(nowUtc, System.Globalization.CultureInfo.InvariantCulture));
        await using var database = Create(time);

        Assert.Equal(TimeSpan.FromHours(hours), database.Services.GetRequiredService<BackupService>().UntilNext());
    }
}
