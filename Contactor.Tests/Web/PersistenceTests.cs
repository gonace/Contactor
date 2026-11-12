using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class PersistenceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_CreateFolderAndDatabase_WhenNeitherExists()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        Assert.True(File.Exists(database.FilePath));
    }

    [Fact]
    public async Task Should_SeedEightActiveRelaysAndTheInput()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var relays = await db.Relays.OrderBy(r => r.Id).ToListAsync(Cancel);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], relays.Select(r => r.Id));
        Assert.Equal("Relay 1", relays[0].Name);
        Assert.All(relays, r => Assert.True(r.IsActive));
        Assert.All(relays, r => Assert.Equal(Relay.DefaultMaxOnMinutes, r.MaxOnMinutes));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], await db.RelayRuntimes.OrderBy(r => r.RelayId).Select(r => r.RelayId).ToListAsync(Cancel));
        var input = Assert.Single(await db.Inputs.ToListAsync(Cancel));
        Assert.Equal(("Input 1", true), (input.Name, input.IsActive));
    }

    [Fact]
    public async Task Should_KeepExistingRows_WhenInitializedAgain()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.Relays.SingleAsync(r => r.Id == 4, Cancel);
            relay.Name = "Ventilation – Paint shop";
            relay.IsActive = false;
            await db.SaveChangesAsync(Cancel);
        }

        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

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
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        Assert.Equal("wal", await PragmaAsync(database.ConnectionString, "journal_mode"));
    }

    [Fact]
    public async Task Should_SetBusyTimeout_OnEveryConnection()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        await db.Database.OpenConnectionAsync(Cancel);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal(5000L, await command.ExecuteScalarAsync(Cancel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task Should_RejectRelay_WhenIdIsNotABoardChannel(int id)
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Relays.Add(new Relay { Id = id, Name = "Wrong" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_RejectSchedule_WithoutDays()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule { RelayId = 1, Days = ScheduleDays.None, CreatedBy = "john@example.com" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_StoreScheduleTimesAsLocalHourAndMinute()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
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
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
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

    [Fact]
    public async Task Should_RemoveRowsAboveEight_WhenMigratedBackToEightRelays()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 16, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan", StartDelaySec = 3 });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 10, RoleName = "Heat", StartDelaySec = 3 });
            group.Members.Add(new RelayGroupMember { RelayId = 12, Position = 3, RoleName = "Lamp", StartDelaySec = 3 });
            db.RelayGroups.Add(group);
            db.Schedules.Add(new Schedule
            {
                RelayId = 12, Days = ScheduleDays.All, OnTime = new TimeOnly(6, 0), OffTime = new TimeOnly(8, 0), IsEnabled = true, CreatedBy = "john@example.com",
            });
            db.Schedules.Add(new Schedule
            {
                RelayId = 5, Days = ScheduleDays.All, OnTime = new TimeOnly(6, 0), OffTime = new TimeOnly(8, 0), IsEnabled = true, CreatedBy = "john@example.com",
            });
            await db.SaveChangesAsync(Cancel);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261108174132_AddGroupSchedules", cancellationToken: Cancel);
        }

        Assert.Equal("1,2,3,4,5,6,7,8", await ScalarAsync(database.ConnectionString, "SELECT group_concat(\"Id\") FROM (SELECT \"Id\" FROM \"Relay\" ORDER BY \"Id\");"));
        Assert.Equal(8L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM \"RelayRuntime\" WHERE \"RelayId\" <= 8;"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM \"RelayRuntime\" WHERE \"RelayId\" > 8;"));
        Assert.Equal("1:1", await ScalarAsync(database.ConnectionString, "SELECT group_concat(\"RelayId\" || ':' || \"Position\") FROM \"RelayGroupMember\";"));
        Assert.Equal(5L, await ScalarAsync(database.ConnectionString, "SELECT \"RelayId\" FROM \"Schedule\";"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM \"RelayGroup\";"));
    }

    [Fact]
    public async Task Should_SeedOneInputPerBoardInput_AndKeepTheRest()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel, inputCount: 4);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel, inputCount: 1);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var inputs = await db.Inputs.OrderBy(i => i.Id).ToListAsync(Cancel);
        Assert.Equal(["Input 1", "Input 2", "Input 3", "Input 4"], inputs.Select(i => i.Name));
        Assert.All(inputs, i => Assert.True(i.IsActive));
    }

    // Before, there was one input row named "Input", and only input 1 was logged, without its number.
    [Fact]
    public async Task Should_NumberTheEarlierInputAndItsEvents_WhenUpgraded()
    {
        await using var database = TestDatabase.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112114304_AddInstallation", cancellationToken: Cancel);
        }

        await ExecuteAsync(database.ConnectionString, "INSERT INTO \"Input\" (\"Id\", \"Name\", \"ClosedText\", \"OpenText\", \"NotifyOnOpen\", \"NotifyOnClose\") VALUES (1, 'Input', 'Closed', 'Open', 0, 0);");
        await ExecuteAsync(database.ConnectionString, "INSERT INTO \"EventLog\" (\"TimestampUtc\", \"Actor\", \"Action\", \"Details\") VALUES ('2026-11-01 10:00:00', 'external', 'input-changed', '{\"on\":true}'), ('2026-11-01 10:01:00', 'system', 'connection-lost', NULL);");

        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel, inputCount: 1);

        Assert.Equal("Input 1", await ScalarAsync(database.ConnectionString, "SELECT \"Name\" FROM \"Input\" WHERE \"Id\" = 1;"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString, "SELECT \"IsActive\" FROM \"Input\" WHERE \"Id\" = 1;"));
        Assert.Equal("1", await ScalarAsync(database.ConnectionString, "SELECT group_concat(coalesce(\"InputId\", '-')) FROM \"EventLog\" WHERE \"Action\" = 'input-changed';"));
        Assert.Equal("-", await ScalarAsync(database.ConnectionString, "SELECT coalesce(\"InputId\", '-') FROM \"EventLog\" WHERE \"Action\" = 'connection-lost';"));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Cancel);
    }

    private static Task<object?> PragmaAsync(string connectionString, string pragma) =>
        ScalarAsync(connectionString, $"PRAGMA {pragma};");
}
