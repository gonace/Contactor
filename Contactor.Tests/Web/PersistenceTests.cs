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
        db.Relays.Add(new Relay { Id = id, BoardId = 1, Channel = 9, Name = "Wrong" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(3)]
    public async Task Should_RejectRelay_WhenItsChannelIsNotOneOfTheBoards(int channel)
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Relays.Add(new Relay { Id = 9, BoardId = 1, Channel = channel, Name = "Wrong" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_SeedTheBoardWithItsRows()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(relayCount: 8, inputCount: 2, Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.SingleAsync(Cancel);
        Assert.Equal(("", "", 1), (board.Name, board.Type, board.SortOrder));
        Assert.Equal(board.Id, (await db.BoardRuntimes.SingleAsync(Cancel)).BoardId);
        Assert.All(await db.Relays.ToListAsync(Cancel), r => Assert.Equal((board.Id, r.Id), (r.BoardId, r.Channel)));
        Assert.All(await db.Inputs.ToListAsync(Cancel), i => Assert.Equal((board.Id, i.Id), (i.BoardId, i.Channel)));
    }

    [Fact]
    public async Task Should_AddNoBoard_BeforeOneIsChosen()
    {
        await using var database = TestDatabase.Create();

        await database.Services.InitializeDatabaseAsync(relayCount: 0, inputCount: 0, Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.AnyAsync(Cancel));
        Assert.False(await db.BoardRuntimes.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_DeleteABoardsEvents_WithTheBoard()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 2, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = Earlier, BoardId = 1, RelayId = 1, Actor = EventActors.System, Action = EventActions.On });
            db.EventLog.Add(new EventLogEntry { TimestampUtc = Earlier, Actor = EventActors.System, Action = EventActions.On });
            await db.SaveChangesAsync(Cancel);
        }

        await ExecuteAsync(database.ConnectionString, "DELETE FROM \"RelayRuntime\"; DELETE FROM \"Board\" WHERE \"Id\" = 1;");

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await check.EventLog.SingleAsync(Cancel)).BoardId);
        Assert.False(await check.Relays.AnyAsync(Cancel));
        Assert.False(await check.BoardRuntimes.AnyAsync(Cancel));
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
            var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
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
        await database.Services.InitializeDatabaseAsync(relayCount: 8, inputCount: 4, Cancel);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, inputCount: 1, Cancel);

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

        await database.Services.InitializeDatabaseAsync(relayCount: 8, inputCount: 1, Cancel);

        Assert.Equal("Input 1", await ScalarAsync(database.ConnectionString, "SELECT \"Name\" FROM \"Input\" WHERE \"Id\" = 1;"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString, "SELECT \"IsActive\" FROM \"Input\" WHERE \"Id\" = 1;"));
        Assert.Equal("1", await ScalarAsync(database.ConnectionString, "SELECT group_concat(coalesce(\"InputId\", '-')) FROM \"EventLog\" WHERE \"Action\" = 'input-changed';"));
        Assert.Equal("-", await ScalarAsync(database.ConnectionString, "SELECT coalesce(\"InputId\", '-') FROM \"EventLog\" WHERE \"Action\" = 'connection-lost';"));
    }

    // Before the board had a row of its own: one board, its address, login and runtime in singletons.
    private const string BeforeBoards = "20261113172648_AddTailscaleSetting";

    private static readonly DateTime Earlier = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Should_MoveTheBoardAndEverythingOnIt_WhenUpgraded()
    {
        await using var database = await UpgradeAsync(
            """
            INSERT INTO "Installation" ("Id", "BoardType", "AccessType", "SignInType", "SetupDoneUtc") VALUES (1, 'vm204', 'local', 'none', '2026-11-01 08:00:00');
            INSERT INTO "BoardSetting" ("Id", "Host", "Port", "Username", "Password") VALUES (1, '192.168.1.50', 80, 'admin', 'secret');
            INSERT INTO "SchedulerState" ("Id", "LastTickUtc") VALUES (1, '2026-11-13 10:00:00');
            INSERT INTO "BoardWatchState" ("Id", "LastContactUtc", "LongGapEndedUtc") VALUES (1, '2026-11-13 10:05:00', '2026-11-12 07:00:00');
            INSERT INTO "Relay" ("Id", "Name", "IsActive", "MaxOnMinutes", "SortOrder") VALUES
                (1, 'Fan', 1, 120, 1), (2, 'Heat', 1, 120, 2), (3, 'Compressor', 1, 120, 3), (4, 'Lighting', 0, NULL, 4);
            INSERT INTO "RelayRuntime" ("RelayId", "OnSinceUtc", "AutoOffAtUtc", "TurnedOnBy", "Version") VALUES
                (1, NULL, NULL, NULL, 0), (2, NULL, NULL, NULL, 0), (3, '2026-11-13 09:00:00', NULL, 'schedule', 3), (4, NULL, NULL, NULL, 0);
            INSERT INTO "Input" ("Id", "Name", "IsActive", "NotifyOnOpen", "NotifyOnClose") VALUES (1, 'Door', 1, 0, 0), (2, 'Input 2', 1, 0, 0);
            INSERT INTO "RelayGroup" ("Id", "Name", "Icon", "MaxOnMinutes", "IsActive", "SortOrder") VALUES (1, 'Fan heater', 'fan', 120, 1, 1);
            INSERT INTO "RelayGroupMember" ("GroupId", "RelayId", "Position", "RoleName", "StartDelaySec", "RunOnSec") VALUES (1, 1, 1, 'Fan', 3, 300), (1, 2, 2, 'Heat', 3, 0);
            INSERT INTO "GroupRuntime" ("GroupId", "Phase", "TargetLevel", "PendingOn") VALUES (1, 'idle', 0, 0);
            INSERT INTO "Schedule" ("RelayId", "GroupId", "Days", "OnTime", "OffTime", "IsEnabled", "CreatedBy") VALUES
                (3, NULL, 31, '06:30', '08:00', 1, 'jane@example.com'), (NULL, 1, 31, '06:00', '07:30', 1, 'alex@example.com');
            INSERT INTO "InputAction" ("InputId", "RelayId", "GroupId", "WhenClosed", "WhenOpened") VALUES (1, 4, NULL, 1, 0), (2, NULL, 1, 1, NULL);
            INSERT INTO "EventLog" ("TimestampUtc", "RelayId", "GroupId", "InputId", "Actor", "Action", "Details") VALUES
                ('2026-11-13 09:00:00', 3, NULL, NULL, 'schedule', 'on', NULL),
                ('2026-11-13 09:01:00', NULL, 1, NULL, 'alex@example.com', 'group-create', '{"name":"Fan heater"}'),
                ('2026-11-13 09:02:00', NULL, NULL, 1, 'external', 'input-changed', '{"on":true}'),
                ('2026-11-13 09:03:00', NULL, NULL, NULL, 'system', 'connection-lost', NULL),
                ('2026-11-13 09:04:00', NULL, NULL, NULL, 'jane@example.com', 'board-login-changed', NULL);
            """);

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = await db.Boards.SingleAsync(Cancel);
            Assert.Equal((1, "", "vm204", 1), (board.Id, board.Name, board.Type, board.SortOrder));
            Assert.Equal(("192.168.1.50", (int?)80, "admin", "secret"), (board.Host, board.Port, board.Username, board.Password));
            var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
            Assert.Equal(1, runtime.BoardId);
            Assert.Equal(Earlier, runtime.LastTickUtc);
            Assert.Equal(Earlier.AddMinutes(5), runtime.LastContactUtc);
            Assert.Equal(new DateTime(2026, 11, 12, 7, 0, 0, DateTimeKind.Utc), runtime.LongGapEndedUtc);
            Assert.Equal(["1:1:1", "2:1:2", "3:1:3", "4:1:4"], await db.Relays.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.BoardId}:{r.Channel}").ToListAsync(Cancel));
            Assert.Equal(["1:1:1", "2:1:2"], await db.Inputs.OrderBy(i => i.Id).Select(i => $"{i.Id}:{i.BoardId}:{i.Channel}").ToListAsync(Cancel));
            Assert.Equal(1, (await db.RelayGroups.SingleAsync(Cancel)).BoardId);
            Assert.Equal(5, await db.EventLog.CountAsync(e => e.BoardId == 1, Cancel));
            var installation = await db.Installations.SingleAsync(Cancel);
            Assert.Equal(("local", "none"), (installation.AccessType, installation.SignInType));
            Assert.NotNull(installation.SetupDoneUtc);
        }

        // Nothing on the board went with the tables rebuilt around it.
        Assert.Equal("4|2|2|1|2|2", await ScalarAsync(
            database.ConnectionString,
            """
            SELECT (SELECT COUNT(*) FROM "RelayRuntime") || '|' || (SELECT COUNT(*) FROM "RelayGroupMember") || '|' ||
                   (SELECT COUNT(*) FROM "Schedule") || '|' || (SELECT COUNT(*) FROM "GroupRuntime") || '|' ||
                   (SELECT COUNT(*) FROM "InputAction") || '|' || (SELECT COUNT(*) FROM "Input");
            """));
        Assert.Equal("3|schedule|3", await ScalarAsync(database.ConnectionString, "SELECT \"RelayId\" || '|' || \"TurnedOnBy\" || '|' || \"Version\" FROM \"RelayRuntime\" WHERE \"OnSinceUtc\" IS NOT NULL;"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM sqlite_master WHERE \"name\" IN ('BoardSetting', 'SchedulerState', 'BoardWatchState');"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString, "SELECT COUNT(*) FROM pragma_table_info('Installation') WHERE \"name\" = 'BoardType';"));

        // And the app seeds nothing twice on it.
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 2, Cancel);
        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal((1, 1, 4, 2), (await check.Boards.CountAsync(Cancel), await check.BoardRuntimes.CountAsync(Cancel), await check.Relays.CountAsync(Cancel), await check.Inputs.CountAsync(Cancel)));
    }

    // A new installation, or one part of the way through the setup guide: no board until the guide chooses one.
    [Fact]
    public async Task Should_AddNoBoard_WhenUpgradedWithoutOne()
    {
        await using var database = await UpgradeAsync(
            """
            INSERT INTO "Installation" ("Id", "AccessType", "SignInType") VALUES (1, 'local', 'none');
            INSERT INTO "SchedulerState" ("Id", "LastTickUtc") VALUES (1, NULL);
            INSERT INTO "EventLog" ("TimestampUtc", "Actor", "Action") VALUES ('2026-11-13 09:00:00', 'system', 'connection-lost');
            """);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.AnyAsync(Cancel));
        Assert.False(await db.BoardRuntimes.AnyAsync(Cancel));
        Assert.Null((await db.EventLog.SingleAsync(Cancel)).BoardId);
        Assert.NotNull(await db.Installations.SingleAsync(Cancel));
    }

    // An installation in use from before the setup guide chose the model: the board's rows move, and its model is
    // chosen at start as before (see SetupTests).
    [Fact]
    public async Task Should_MoveTheRelays_WhenUpgradedWithoutAModel()
    {
        await using var database = await UpgradeAsync(
            """
            INSERT INTO "Installation" ("Id", "SetupDoneUtc", "AccessType", "SignInType") VALUES (1, '2026-11-01 08:00:00', 'cloudflare', 'cloudflare-access');
            INSERT INTO "SchedulerState" ("Id", "LastTickUtc") VALUES (1, '2026-11-13 10:00:00');
            INSERT INTO "Relay" ("Id", "Name", "IsActive", "MaxOnMinutes", "SortOrder") VALUES (1, 'Relay 1', 1, 120, 1), (8, 'Relay 8', 1, 120, 8);
            INSERT INTO "Input" ("Id", "Name", "IsActive", "NotifyOnOpen", "NotifyOnClose") VALUES (1, 'Input 1', 1, 0, 0);
            """);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.SingleAsync(Cancel);
        Assert.Equal((1, "", ""), (board.Id, board.Name, board.Type));
        Assert.Equal(((string?)null, (int?)null, (string?)null, (string?)null), (board.Host, board.Port, board.Username, board.Password));
        var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
        Assert.Equal((Earlier, (DateTime?)null, (DateTime?)null), (runtime.LastTickUtc!.Value, runtime.LastContactUtc, runtime.LongGapEndedUtc));
        Assert.Equal(["1:1:1", "8:1:8"], await db.Relays.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.BoardId}:{r.Channel}").ToListAsync(Cancel));
        Assert.Equal("1:1:1", await db.Inputs.Select(i => $"{i.Id}:{i.BoardId}:{i.Channel}").SingleAsync(Cancel));
    }

    // No address saved in the app, and neither the scheduler nor the recorder had saved anything: the settings keep
    // applying, and the runtime starts empty.
    [Fact]
    public async Task Should_LeaveTheAddressToTheSettings_WhenNoneWasSaved()
    {
        await using var database = await UpgradeAsync(
            """
            INSERT INTO "Installation" ("Id", "BoardType") VALUES (1, 'vm201');
            """);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.SingleAsync(Cancel);
        Assert.Equal(("vm201", (string?)null, (int?)null, (string?)null), (board.Type, board.Host, board.Port, board.Password));
        var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
        Assert.Equal(((DateTime?)null, (DateTime?)null, (DateTime?)null), (runtime.LastTickUtc, runtime.LastContactUtc, runtime.LongGapEndedUtc));
    }

    [Fact]
    public async Task Should_PutTheBoardBackInTheSingletons_WhenMigratedBack()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 1, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = await db.Boards.SingleAsync(Cancel);
            (board.Type, board.Host, board.Port, board.Username, board.Password) = ("vm204", "192.168.1.50", 80, "admin", "secret");
            var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
            (runtime.LastTickUtc, runtime.LastContactUtc, runtime.LongGapEndedUtc) = (Earlier, Earlier.AddMinutes(5), Earlier.AddDays(-1));
            db.EventLog.Add(new EventLogEntry { TimestampUtc = Earlier, BoardId = board.Id, RelayId = 1, Actor = EventActors.System, Action = EventActions.On });
            var group = new RelayGroup { BoardId = board.Id, Name = "Fan heater", Icon = GroupIcons.Fan };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan", StartDelaySec = 3 });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat", StartDelaySec = 3 });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeBoards, cancellationToken: Cancel);
        }

        var cs = database.ConnectionString;
        Assert.Equal("vm204", await ScalarAsync(cs, "SELECT \"BoardType\" FROM \"Installation\";"));
        Assert.Equal("192.168.1.50|80|admin|secret", await ScalarAsync(cs, "SELECT \"Host\" || '|' || \"Port\" || '|' || \"Username\" || '|' || \"Password\" FROM \"BoardSetting\";"));
        Assert.Equal("2026-11-13 10:00:00", await ScalarAsync(cs, "SELECT \"LastTickUtc\" FROM \"SchedulerState\";"));
        Assert.Equal("2026-11-13 10:05:00|2026-11-12 10:00:00", await ScalarAsync(cs, "SELECT \"LastContactUtc\" || '|' || \"LongGapEndedUtc\" FROM \"BoardWatchState\";"));
        Assert.Equal("4|1|1|2|1", await ScalarAsync(
            cs,
            """
            SELECT (SELECT COUNT(*) FROM "Relay") || '|' || (SELECT COUNT(*) FROM "Input") || '|' || (SELECT COUNT(*) FROM "EventLog") || '|' ||
                   (SELECT COUNT(*) FROM "RelayGroupMember") || '|' || (SELECT COUNT(*) FROM "RelayGroup");
            """));
        Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM sqlite_master WHERE \"name\" IN ('Board', 'BoardRuntime');"));
    }

    // A database as the version before the board's own row left it, with the rows the SQL adds, brought up to date.
    private static async Task<TestDatabase> UpgradeAsync(string sql)
    {
        var database = TestDatabase.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeBoards, cancellationToken: Cancel);
        }

        await ExecuteAsync(database.ConnectionString, sql);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.MigrateAsync(Cancel);
        }

        return database;
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
