using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// A group's schedules against the simulator: the scheduler starts the group in its order and stops it with its
/// run-on. The rig starts on Wednesday 4 November 2026 at 14:00 local; the fan heater is relay 1 (Fläkt, 5 min
/// run-on) and relay 2 (Värme).
/// </summary>
public sealed class GroupScheduleTests
{
    private const string Ted = "ted@example.com";

    private static readonly GroupDraft FanHeater = new(
        "Värmefläkt",
        GroupIcons.Fan,
        120,
        [new GroupMemberDraft(1, "Fläkt", 3, 300), new GroupMemberDraft(2, "Värme", 3, 0)]);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_StartTheGroupInOrder_AndStopItWithItsRunOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        var scheduleId = await AddScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);

        await TickAtAsync(rig, 14, 5);
        await StartedAsync(rig);
        var runtime = await RuntimeAsync(rig, groupId);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));

        await TickAtAsync(rig, 14, 10);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
        await rig.Groups.StepAsync(Cancel);
        Assert.True(rig.Client.State.IsOn(1));
        rig.Time.Now = Local(14, 15).AddSeconds(1);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);

        var events = (await rig.EventsAsync()).Where(e => e.GroupId == groupId).ToList();
        Assert.Contains(events, e => e is { Action: EventActions.GroupOn, Actor: EventActors.Schedule });
        Assert.Contains(events, e => e is { Action: EventActions.GroupOff, Actor: EventActors.Schedule });
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
        Assert.NotEqual(0, scheduleId);
    }

    [Fact]
    public async Task Should_StartAGroupWhoseRunIsGoing_AtStart()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 13, 0, 15, 0);

        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_CatchUpAMissedOff_ForAGroupTheScheduleStarted()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await StartedAsync(rig);

        await TickAtAsync(rig, 14, 30);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
        Assert.Equal(GroupPhases.Stopping, (await RuntimeAsync(rig, groupId)).Phase);
    }

    [Fact]
    public async Task Should_LeaveAGroupAPersonStarted_OnAMissedOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Groups.StartGroupAsync(groupId, Ted, Cancel);
        await StartedAsync(rig);
        await TickAtAsync(rig, 14, 5);

        await TickAtAsync(rig, 14, 30);
        await Task.Delay(200, Cancel);

        Assert.True(rig.Client.State.IsOn(2));
        var runtime = await RuntimeAsync(rig, groupId);
        Assert.Equal(Ted, runtime.TurnedOnBy);
        Assert.NotNull(runtime.AutoOffAtUtc);
    }

    [Fact]
    public async Task Should_StopAGroupTheScheduleStarted_WhenTheScheduleIsDeleted()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        var scheduleId = await AddScheduleAsync(rig, groupId, 14, 5, 18, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await StartedAsync(rig);

        await rig.Database.Services.GetRequiredService<SchedulesHandler>().DeleteAsync(scheduleId, Ted, Cancel);
        await TickAtAsync(rig, 14, 20);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
    }

    [Fact]
    public async Task Should_DeleteTheSchedules_WithTheGroup()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 6, 0, 7, 0);

        Assert.Null(await rig.GroupSetup.DeleteAsync(groupId, Ted, Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Empty(await db.Schedules.ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_SaveAndListAGroupsSchedule_AndNameTheGroupInTheLog()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        var schedules = rig.Database.Services.GetRequiredService<SchedulesHandler>();

        Assert.Null(await schedules.SaveAsync(
            new ScheduleModel(0, null, ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0), true, groupId), Ted, Cancel));

        var saved = Assert.Single(await schedules.ListForGroupAsync(groupId, Cancel));
        Assert.Equal((null, groupId), (saved.RelayId, saved.GroupId));
        Assert.Empty(await schedules.ListAsync(1, Cancel));
        var rows = await rig.Database.Services.GetRequiredService<EventsHandler>()
            .GetAsync(EventFilter.All, beforeId: null, 10, Cancel, groupId: groupId);
        Assert.Equal(("lade till schema mån–fre 06:30–08:00", "Värmefläkt"), (rows[0].View.What, rows[0].View.Target));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3, 1)]
    public async Task Should_RefuseASchedule_ForNeitherOrBoth(int? relayId, int? groupId)
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig);
        var schedules = rig.Database.Services.GetRequiredService<SchedulesHandler>();

        var error = await schedules.SaveAsync(
            new ScheduleModel(0, relayId, ScheduleDays.All, new TimeOnly(6, 0), new TimeOnly(7, 0), true, groupId), Ted, Cancel);

        Assert.Equal("Välj ett relä eller en grupp.", error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3, 1)]
    public async Task Should_KeepAScheduleOnOneRelayOrGroup_InTheDatabase(int? relayId, int? groupId)
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig);
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            GroupId = groupId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(6, 0),
            OffTime = new TimeOnly(7, 0),
            IsEnabled = true,
            CreatedBy = Ted,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    [Fact]
    public async Task Should_LetOnWin_WhenTheGroupsSchedulesOverlap()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await AddScheduleAsync(rig, groupId, 14, 8, 14, 20);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await StartedAsync(rig);

        await TickAtAsync(rig, 14, 10);
        await Task.Delay(200, Cancel);
        Assert.True(rig.Client.State.IsOn(2));

        await TickAtAsync(rig, 14, 20);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
    }

    [Fact]
    public async Task Should_StopAGroupAPersonStarted_AtALiveOffTime()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Groups.StartGroupAsync(groupId, Ted, Cancel);
        await StartedAsync(rig);
        await TickAtAsync(rig, 14, 5);

        await TickAtAsync(rig, 14, 10);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
    }

    [Fact]
    public async Task Should_NotRestartAGroup_ThatAPersonsAutoOffStoppedDuringTheRun()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 18, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Groups.StartGroupAsync(groupId, Ted, Cancel);
        await StartedAsync(rig);
        await TickAtAsync(rig, 14, 5);

        // Ted's 2 h end at 16:00: the group stops with its run-on, and the schedule leaves it off.
        rig.Time.Now = Local(16, 1);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now = Local(16, 7);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
        await rig.Groups.StepAsync(Cancel);
        await TickAtAsync(rig, 16, 8);
        await TickAtAsync(rig, 17, 0);
        await Task.Delay(200, Cancel);

        Assert.Equal(0, rig.Client.State.Outputs);
    }

    [Fact]
    public async Task Should_StartARunThatBeganWhileDisconnected_OnReconnect()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 15, 0);
        await rig.Scheduler.TickAsync(Cancel);

        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await TickAtAsync(rig, 14, 5);
        rig.Time.Now = Local(14, 20);
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        Assert.Equal(EventActors.Schedule, (await RuntimeAsync(rig, groupId)).TurnedOnBy);
    }

    [Fact]
    public async Task Should_CarryOnFromARunOn_AtOnTime()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var groupId = await CreateAsync(rig);
        await AddScheduleAsync(rig, groupId, 14, 5, 15, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Groups.StartGroupAsync(groupId, Ted, Cancel);
        await StartedAsync(rig);
        await rig.Groups.StopGroupAsync(groupId, Ted, Cancel);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
        await rig.Groups.StepAsync(Cancel);
        Assert.True(rig.Client.State.IsOn(1));

        await TickAtAsync(rig, 14, 5);
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        var runtime = await RuntimeAsync(rig, groupId);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));
    }

    [Fact]
    public async Task Should_KeepRelaySchedules_WhenTheDatabaseIsUpgraded()
    {
        await using var database = TestDatabase.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261108155408_AddRelayGroups", Cancel);

            // Raw SQL: the model of this version no longer matches the old table. Constant SQL, no input.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Relay\" (\"Id\", \"Name\", \"IsActive\", \"MaxOnMinutes\", \"SortOrder\") VALUES (3, 'Relä 3', 1, 120, 3);"
                + "INSERT INTO \"Schedule\" (\"RelayId\", \"Days\", \"OnTime\", \"OffTime\", \"IsEnabled\", \"CreatedBy\") VALUES (3, 31, '06:30', '08:00', 1, 'ted@example.com');",
                Cancel);
            await migrator.MigrateAsync(cancellationToken: Cancel);
        }

        await using var after = await database.Factory.CreateDbContextAsync(Cancel);
        var schedule = Assert.Single(await after.Schedules.ToListAsync(Cancel));
        Assert.Equal(((int?)3, (int?)null, ScheduleDays.Weekdays, new TimeOnly(6, 30)), (schedule.RelayId, schedule.GroupId, schedule.Days, schedule.OnTime));
    }

    // The fan is confirmed, its 3 s pass, then the heat, confirmed too: as the sequencer does every second.
    private static async Task StartedAsync(BoardTestRig rig)
    {
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        // The board confirmed the heat: the start is done.
        await rig.Groups.StepAsync(Cancel);
    }

    private static async Task<int> CreateAsync(BoardTestRig rig)
    {
        var (id, error) = await rig.GroupSetup.CreateAsync(FanHeater, Ted, Cancel);
        Assert.Null(error);
        return id!.Value;
    }

    private static async Task<int> AddScheduleAsync(BoardTestRig rig, int groupId, int onHour, int onMinute, int offHour, int offMinute)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var schedule = new Schedule
        {
            GroupId = groupId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(onHour, onMinute),
            OffTime = new TimeOnly(offHour, offMinute),
            IsEnabled = true,
            CreatedBy = Ted,
        };
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(Cancel);
        return schedule.Id;
    }

    private static async Task<GroupRuntime> RuntimeAsync(BoardTestRig rig, int groupId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.GroupRuntimes.SingleAsync(r => r.GroupId == groupId, Cancel);
    }

    // 14:00 local is 13:00 UTC on the rig's day.
    private static DateTimeOffset Local(int hour, int minute)
    {
        return new DateTimeOffset(2026, 11, 4, hour - 1, minute, 0, TimeSpan.Zero);
    }

    private static async Task TickAtAsync(BoardTestRig rig, int hour, int minute)
    {
        rig.Time.Now = Local(hour, minute);
        await rig.Scheduler.TickAsync(Cancel);
    }
}
