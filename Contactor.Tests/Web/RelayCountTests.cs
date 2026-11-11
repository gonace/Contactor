using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>Boards with other than 8 relays: the database, the lists and the commands follow the board's count.</summary>
public sealed class RelayCountTests
{
    private const string John = "john@example.com";

    // Wednesday 11 November 2026, 11:00 in Stockholm.
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Should_SeedOneRelayPerChannelOfTheBoard(int count)
    {
        await using var database = Create(new FakeRelayBoard(count));

        await database.Services.InitializeDatabaseAsync(Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(Enumerable.Range(1, count), await db.Relays.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Cancel));
        Assert.Equal(count, await db.RelayRuntimes.CountAsync(Cancel));
    }

    [Fact]
    public async Task Should_KeepRelaysABiggerBoardLeft_ButNotOfferThem()
    {
        // The database was a VM201's (8 relays); the board now has 4.
        await using var database = Create(new FakeRelayBoard(4));
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await database.Services.InitializeDatabaseAsync(Cancel);

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.Equal(8, await db.Relays.CountAsync(Cancel));
        }

        var settings = database.Services.GetRequiredService<RelaySettingsHandler>();
        Assert.NotNull(await settings.GetAsync(4, Cancel));
        Assert.Null(await settings.GetAsync(5, Cancel));

        var groups = database.Services.GetRequiredService<GroupsHandler>();
        Assert.Equal([1, 2, 3, 4], (await groups.CandidatesAsync(Cancel)).Select(r => r.RelayId));
        Assert.Equal([1, 2, 3, 4], (await groups.FreeRelaysAsync(Cancel)).Select(r => r.Id).Order());

        var schedules = database.Services.GetRequiredService<SchedulesHandler>();
        var error = await schedules.SaveAsync(new ScheduleModel(0, 5, ScheduleDays.All, new TimeOnly(6, 0), new TimeOnly(7, 0), true), John, Cancel);
        Assert.Equal("The relay doesn't exist.", error);
        Assert.Equal("The relay doesn't exist.", await settings.SaveAsync(new RelaySettings(5, "Pump", true, null), John, Cancel));
    }

    [Fact]
    public async Task Should_KeepAGroupOff_AndLetItBeDeleted_WhenItHasARelayTheBoardDoesNotHave()
    {
        // Fan on relay 4, Heat on relay 5, and the board now has 4 relays: the group can't keep its order.
        var board = new FakeRelayBoard(4);
        await using var database = Create(board);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        var groupId = await AddGroupAsync(database, (4, "Fan"), (5, "Heat"));
        var sequencer = database.Services.GetRequiredService<GroupSequencer>();
        var groups = database.Services.GetRequiredService<GroupsHandler>();

        await sequencer.StartGroupAsync(groupId, John, Cancel);
        Assert.True(board.State.Relays.IsEmpty);

        // Turned on at the board itself: the next step turns it off again.
        await board.TurnOnAsync(ChannelMask.Of(4), Cancel);
        await sequencer.StepAsync(Cancel);

        Assert.True(board.State.Relays.IsEmpty);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var trip = await db.EventLog.SingleAsync(e => e.Action == EventActions.InterlockTrip, Cancel);
            Assert.Equal((groupId, EventActors.System), (trip.GroupId!.Value, trip.Actor));
            Assert.Equal("""{"missing":"Heat","off":["Fan"]}""", trip.Details);
        }

        var view = Assert.Single(await groups.ListAsync(Cancel));
        Assert.Equal(5, view.MissingRelay(board.Capabilities.RelayCount));
        Assert.Null(await groups.DeleteAsync(groupId, John, Cancel));
    }

    [Fact]
    public async Task Should_ScheduleAndAutoOffOnlyTheBoardsRelays()
    {
        var board = new FakeRelayBoard(4);
        await using var database = Create(board);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            foreach (var relayId in new[] { 2, 6 })
            {
                db.Schedules.Add(new Schedule
                {
                    RelayId = relayId, Days = ScheduleDays.All, OnTime = new TimeOnly(6, 0), OffTime = new TimeOnly(20, 0), IsEnabled = true, CreatedBy = John,
                });
            }

            var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 7, Cancel);
            runtime.OnSinceUtc = Now.UtcDateTime.AddHours(-3);
            runtime.AutoOffAtUtc = Now.UtcDateTime.AddHours(-1);
            await db.SaveChangesAsync(Cancel);
        }

        await database.Services.GetRequiredService<SchedulerService>().TickAsync(Cancel);
        var turnedOff = await database.Services.GetRequiredService<AutoOffService>().CheckAsync(Cancel);

        Assert.Equal(ChannelMask.Of(2), board.State.Relays);
        Assert.Empty(turnedOff);
    }

    private static async Task<int> AddGroupAsync(TestDatabase database, params (int RelayId, string Role)[] members)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
        for (var i = 0; i < members.Length; i++)
        {
            group.Members.Add(new RelayGroupMember { RelayId = members[i].RelayId, Position = i + 1, RoleName = members[i].Role, StartDelaySec = 3 });
        }

        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }

    [Fact]
    public async Task Should_SwitchRelaysAboveEight_OnABiggerBoard()
    {
        var board = new FakeRelayBoard(16);
        await using var database = Create(board);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var control = database.Services.GetRequiredService<RelayControl>();

        await control.TurnOnAsync(12, John, Cancel);

        Assert.True(board.State.IsOn(12));
        await control.TurnAllOffAsync(John, Cancel);
        Assert.True(board.State.Relays.IsEmpty);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => control.TurnOnAsync(17, John, Cancel));
    }

    [Fact]
    public async Task Should_GroupRelaysAboveEight_OnABiggerBoard()
    {
        await using var database = Create(new FakeRelayBoard(16));
        await database.Services.InitializeDatabaseAsync(Cancel);
        var groups = database.Services.GetRequiredService<GroupsHandler>();

        var members = Enumerable.Range(1, 10).Select(id => new GroupMemberDraft(id, $"Step {id}")).ToList();
        var (id, error) = await groups.CreateAsync(new GroupDraft("Ten steps", GroupIcons.Bolt, null, members), John, Cancel);

        Assert.Null(error);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(10, await db.RelayGroupMembers.CountAsync(m => m.GroupId == id, Cancel));
    }

    private static TestDatabase Create(IRelayBoard board)
    {
        return TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton(board);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSchedules(configuration);
        });
    }
}
