using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class PersistenceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_CreateFolderAndDatabase_WhenNeitherExists()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(Cancel);

        Assert.True(File.Exists(database.FilePath));
    }

    [Fact]
    public async Task Should_SeedEightActiveRelaysAndTheInput()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var relays = await db.Relays.OrderBy(r => r.Id).ToListAsync(Cancel);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], relays.Select(r => r.Id));
        Assert.Equal("Relay 1", relays[0].Name);
        Assert.All(relays, r => Assert.True(r.IsActive));
        Assert.All(relays, r => Assert.Equal(Relay.DefaultMaxOnMinutes, r.MaxOnMinutes));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], await db.RelayRuntimes.OrderBy(r => r.RelayId).Select(r => r.RelayId).ToListAsync(Cancel));
        var input = Assert.Single(await db.Inputs.ToListAsync(Cancel));
        Assert.Equal(("Closed", "Open"), (input.ClosedText, input.OpenText));
    }

    [Fact]
    public async Task Should_KeepExistingRows_WhenInitializedAgain()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.Relays.SingleAsync(r => r.Id == 4, Cancel);
            relay.Name = "Ventilation – Paint shop";
            relay.IsActive = false;
            await db.SaveChangesAsync(Cancel);
        }

        await database.Services.InitializeDatabaseAsync(Cancel);

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(8, await check.Relays.CountAsync(Cancel));
        Assert.Equal(1, await check.Inputs.CountAsync(Cancel));
        var renamed = await check.Relays.SingleAsync(r => r.Id == 4, Cancel);
        Assert.Equal("Ventilation – Paint shop", renamed.Name);
        Assert.False(renamed.IsActive);
    }

    [Fact]
    public async Task Should_UseWalJournal()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);

        Assert.Equal("wal", await PragmaAsync(database.ConnectionString, "journal_mode"));
    }

    [Fact]
    public async Task Should_SetBusyTimeout_OnEveryConnection()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        await db.Database.OpenConnectionAsync(Cancel);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal(5000L, await command.ExecuteScalarAsync(Cancel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Should_RejectRelay_WhenIdIsNotABoardChannel(int id)
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Relays.Add(new Relay { Id = id, Name = "Fel" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_RejectSchedule_WithoutDays()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule { RelayId = 1, Days = ScheduleDays.None, CreatedBy = "john@example.com" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_StoreScheduleTimesAsLocalHourAndMinute()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Schedules.Add(new Schedule
            {
                RelayId = 1,
                Days = ScheduleDays.Weekdays,
                OnTime = new TimeOnly(6, 30),
                OffTime = new TimeOnly(8, 0),
                IsEnabled = true,
                CreatedBy = "john@example.com",
            });
            await db.SaveChangesAsync(Cancel);
        }

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OnTime || '-' || OffTime FROM Schedule;";
        Assert.Equal("06:30-08:00", await command.ExecuteScalarAsync(Cancel));

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        var schedule = await check.Schedules.SingleAsync(Cancel);
        Assert.Equal(new TimeOnly(6, 30), schedule.OnTime);
    }

    [Fact]
    public async Task Should_ReadTimesBackAsUtc()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        var timestamp = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = timestamp, Actor = EventActors.System, Action = EventActions.On });
            await db.SaveChangesAsync(Cancel);
        }

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = await check.EventLog.SingleAsync(Cancel);

        Assert.Equal(DateTimeKind.Utc, entry.TimestampUtc.Kind);
        Assert.Equal(timestamp, entry.TimestampUtc);
    }

    private static async Task<object?> PragmaAsync(string connectionString, string pragma)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return await command.ExecuteScalarAsync(Cancel);
    }
}
