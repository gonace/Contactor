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
    public async Task Should_LeaveAGroupAlone_WhenItHasARelayTheBoardDoesNotHave()
    {
        var board = new FakeRelayBoard(4);
        await using var database = Create(board);
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        int groupId;
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
            group.Members.Add(new RelayGroupMember { RelayId = 4, Position = 1, RoleName = "Fan", StartDelaySec = 3 });
            group.Members.Add(new RelayGroupMember { RelayId = 5, Position = 2, RoleName = "Heat", StartDelaySec = 3 });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            groupId = group.Id;
        }

        var sequencer = database.Services.GetRequiredService<GroupSequencer>();
        await sequencer.StartGroupAsync(groupId, John, Cancel);
        await sequencer.StepAsync(Cancel);

        Assert.True(board.State.Relays.IsEmpty);
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
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSchedules(configuration);
        });
    }
}
