using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Relay groups against the simulator: the sequence through the real board connection, the interlock on a change made
/// at the board, "All off", and how groups are set up.
/// </summary>
public sealed class GroupSequencerTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static GroupDraft FanHeater => new(
        "Fan heater – Lathe",
        GroupIcons.Fan,
        120,
        [new GroupMemberDraft(1, "Fan", 3, 300), new GroupMemberDraft(2, "Heat", 3, 0)]);

    [Fact]
    public async Task Should_StartTheFanHeaterInOrder()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);

        await rig.Groups.StartGroupAsync(id, John, Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        Assert.False(rig.Simulator.Outputs == 0b11);
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);
        Assert.False(rig.Client.State.IsOn(2));

        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        var started = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.GroupOn);
        Assert.Equal((id, John), (started.GroupId!.Value, started.Actor));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
    }

    [Fact]
    public async Task Should_TellThePages_WhenOnlyTheSequenceMoves()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);
        await rig.Groups.StartGroupAsync(id, John, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        var notified = 0;
        rig.Database.Services.GetRequiredService<RelayChanges>().Changed += (_, _) => notified++;

        // The board confirmed the fan: nothing is sent, but "Starting · step 1" moves on.
        await rig.Groups.StepAsync(Cancel);

        Assert.Equal(1, notified);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task Should_TripTheInterlock_WhenTheHeatIsTurnedOnAtTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(2));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        await rig.Groups.StepAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == Vm201Channel.Mask(1));
        var trip = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
        Assert.Equal((id, EventActors.System, (int?)1), (trip.GroupId!.Value, trip.Actor, trip.RelayId));

        // The fan runs on 5 minutes, then goes off.
        await rig.Groups.StepAsync(Cancel);
        Assert.Contains(await rig.EventsAsync(), e => e.Action == EventActions.RunOnStart);
        rig.Time.Now += TimeSpan.FromMinutes(5);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
    }

    [Fact]
    public async Task Should_StopGroupsWithRunOn_AndSingleRelaysAtOnce_OnAllOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);
        await rig.Simulator.SetOutputsAsync(0b0000_0111);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b0000_0111);

        await rig.Control.TurnAllOffAsync(rig.Live.BoardId, John, Cancel);

        // Relay 3 and the heat at once; the fan keeps running for its run-on.
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b0000_0001);
        await rig.Groups.StepAsync(Cancel);
        var events = await rig.EventsAsync();
        Assert.Contains(events, e => e.Action == EventActions.Off && e.RelayId == 3);
        Assert.Contains(events, e => e.Action == EventActions.GroupOff && e.Actor == John);
        Assert.Contains(events, e => e.Action == EventActions.RunOnStart && e.RelayId == 1);
    }

    [Fact]
    public async Task Should_RefuseToSwitchAGroupsRelayOnItsOwn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);

        await Assert.ThrowsAsync<Contactor.Web.Features.Relays.RelayInGroupException>(() => rig.Control.TurnOnAsync(2, John, Cancel));
        Assert.Equal(
            "Needs Fan to be on.",
            await rig.Groups.SetMemberAsync(await GroupIdAsync(rig), 2, on: true, John, Cancel));
        Assert.False(rig.Client.State.IsOn(2));
    }

    [Fact]
    public async Task Should_NotQueueAnything_WithoutTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => rig.Groups.StartGroupAsync(id, John, Cancel));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.GroupOn);
    }

    [Fact]
    public async Task Should_LogCreateEditAndDelete()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);

        Assert.Null(await rig.GroupSetup.SaveAsync(id, FanHeater with { Name = "Fan heateren" }, John, Cancel));
        Assert.Null(await rig.GroupSetup.DeleteAsync(id, John, Cancel));

        var actions = (await rig.EventsAsync()).Where(e => e.GroupId == id).Select(e => e.Action);
        Assert.Equal([EventActions.GroupCreate, EventActions.GroupEdit, EventActions.GroupDelete], actions);
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Empty(await db.RelayGroupMembers.ToListAsync(Cancel));
        Assert.Empty(await db.GroupRuntimes.ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_RefuseGroupsThatBreakTheRules()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var setup = rig.GroupSetup;

        Assert.Equal("Choose at least 2 relays.", (await setup.CreateAsync(FanHeater with { Members = [new(1, "Fan")] }, John, Cancel)).Error);
        Assert.Equal("Give the group a name.", (await setup.CreateAsync(FanHeater with { Name = "  " }, John, Cancel)).Error);
        Assert.Equal(
            "The wait on start is 1–60 seconds.",
            (await setup.CreateAsync(FanHeater with { Members = [new(1, "Fan", 0), new(2, "Heat", 61)] }, John, Cancel)).Error);
        Assert.Equal(
            "The run-on can be at most 30 minutes.",
            (await setup.CreateAsync(FanHeater with { Members = [new(1, "Fan", 3, 1801), new(2, "Heat")] }, John, Cancel)).Error);
        Assert.Equal(
            "A relay can only be in the group once.",
            (await setup.CreateAsync(FanHeater with { Members = [new(1, "Fan"), new(1, "Heat")] }, John, Cancel)).Error);
        Assert.Equal(
            "That relay doesn't exist.",
            (await setup.CreateAsync(FanHeater with { Members = [new(1, "Fan"), new(999, "Heat")] }, John, Cancel)).Error);

        await CreateAsync(rig, FanHeater);
        Assert.Equal(
            "Relay 2 is already in Fan heater – Lathe.",
            (await setup.CreateAsync(FanHeater with { Name = "Annan", Members = [new(2, "A"), new(3, "B")] }, John, Cancel)).Error);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(4));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(4));
        Assert.Equal(
            "Turn off Relay 4 first.",
            (await setup.CreateAsync(FanHeater with { Name = "Annan", Members = [new(3, "A"), new(4, "B")] }, John, Cancel)).Error);
    }

    [Fact]
    public async Task Should_OfferOnlyFreeRelays_ForANewGroup()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Schedules.Add(new Schedule
            {
                RelayId = 4,
                Days = ScheduleDays.All,
                OnTime = new TimeOnly(6, 0),
                OffTime = new TimeOnly(7, 0),
                IsEnabled = true,
                CreatedBy = John,
            });
            await db.SaveChangesAsync(Cancel);
        }

        var relays = await rig.GroupSetup.CandidatesAsync(Cancel);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], relays.Select(r => r.RelayId));
        Assert.Equal([3, 5, 6, 7, 8], relays.Where(r => r.IsFree).Select(r => r.RelayId));
        Assert.Equal("Already in Fan heater – Lathe", relays[0].Note);
        Assert.Equal("Has its own schedules", relays[3].Note);
    }

    [Fact]
    public async Task Should_RefuseARelayWithItsOwnSchedules()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Schedules.Add(new Schedule
            {
                RelayId = 2,
                Days = ScheduleDays.All,
                OnTime = new TimeOnly(6, 0),
                OffTime = new TimeOnly(7, 0),
                IsEnabled = true,
                CreatedBy = John,
            });
            await db.SaveChangesAsync(Cancel);
        }

        var (id, error) = await rig.GroupSetup.CreateAsync(FanHeater, John, Cancel);

        Assert.Null(id);
        Assert.Equal("Relay 2 has its own schedules. Delete them first.", error);
    }

    [Fact]
    public async Task Should_ChangeTheOrderOnlyWhileTheGroupIsOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);
        var reversed = FanHeater with { Members = [new(2, "Heat"), new(1, "Fan")] };
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(1));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));

        Assert.StartsWith("Turn off the group", await rig.GroupSetup.SaveAsync(id, reversed, John, Cancel), StringComparison.Ordinal);
        Assert.Equal("Turn off the group before deleting it.", await rig.GroupSetup.DeleteAsync(id, John, Cancel));

        // The waits can change while it runs.
        Assert.Null(await rig.GroupSetup.SaveAsync(id, FanHeater with { Members = [new(1, "Fan", 5, 600), new(2, "Heat", 5)] }, John, Cancel));

        await rig.Simulator.SetOutputsAsync(0);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
        Assert.Null(await rig.GroupSetup.SaveAsync(id, reversed, John, Cancel));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal([2, 1], await db.RelayGroupMembers.OrderBy(m => m.Position).Select(m => m.RelayId).ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_RefuseAScheduleForARelayInAGroup()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);
        var schedules = rig.Database.Services.GetRequiredService<Contactor.Web.Features.Schedules.SchedulesHandler>();

        var error = await schedules.SaveAsync(
            new Contactor.Web.Features.Schedules.ScheduleModel(0, 2, ScheduleDays.All, new TimeOnly(6, 0), new TimeOnly(7, 0), true),
            John,
            Cancel);

        Assert.Equal("This relay is in a group. Add the schedule to the group.", error);
    }

    [Fact]
    public async Task Should_KeepTheRunOn_WhenTheConnectionDropsMidStop()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);
        await rig.Simulator.SetOutputsAsync(0b11);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b11);

        // The heat's OFF goes out; the connection drops before the app looks at the board's answer.
        await rig.Groups.StopGroupAsync(id, John, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Simulator.Outputs == 0b01);
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromMinutes(1);

        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        Assert.True(rig.Client.State.IsOn(1));
        Assert.Contains(await rig.EventsAsync(), e => e.Action == EventActions.RunOnStart && e.RelayId == 1);
        rig.Time.Now += TimeSpan.FromMinutes(5);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
    }

    [Fact]
    public async Task Should_SendTheFanOnceMore_WhenATripsOnWasLostInAReconnect()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);

        // A trip turned the heat off and sent the fan's ON, which was lost with the connection.
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.GroupRuntimes.SingleAsync(r => r.GroupId == id, Cancel);
            runtime.Phase = GroupPhases.Stopping;
            runtime.TargetLevel = 0;
            runtime.PendingPosition = 1;
            runtime.PendingOn = true;
            runtime.PendingSinceUtc = BoardTestRig.Start;
            await db.SaveChangesAsync(Cancel);
        }

        _ = rig.Groups;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromMinutes(1);
        await rig.Groups.StepAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        await rig.Groups.StepAsync(Cancel);
        Assert.Contains(await rig.EventsAsync(), e => e.Action == EventActions.RunOnStart && e.RelayId == 1);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.GroupStartFailed);
    }

    [Fact]
    public async Task Should_KeepThePositionsAndWaitsValid_InTheDatabase()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);

        foreach (var member in new[]
        {
            new RelayGroupMember { GroupId = id, RelayId = 3, Position = 2, RoleName = "Same step" },
            new RelayGroupMember { GroupId = id, RelayId = 3, Position = 3, RoleName = "A", StartDelaySec = 0 },
            new RelayGroupMember { GroupId = id, RelayId = 3, Position = 3, RoleName = "A", RunOnSec = 1801 },
            new RelayGroupMember { GroupId = id, RelayId = 3, Position = 65, RoleName = "A" },
        })
        {
            await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
            db.RelayGroupMembers.Add(member);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        }
    }

    [Fact]
    public async Task Should_DeleteTheMembersAndRuntime_WithTheGroup()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FanHeater);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.RelayGroups.Where(g => g.Id == id).ExecuteDeleteAsync(Cancel);
        }

        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Empty(await check.RelayGroupMembers.ToListAsync(Cancel));
        Assert.Empty(await check.GroupRuntimes.ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_KeepARelayInOneGroupOnly_InTheDatabase()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var other = new RelayGroup { BoardId = 1, Name = "Annan", Icon = GroupIcons.Bolt };
        other.Members.Add(new RelayGroupMember { RelayId = 2, Position = 1, RoleName = "A" });
        other.Members.Add(new RelayGroupMember { RelayId = 3, Position = 2, RoleName = "B" });
        db.RelayGroups.Add(other);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    private static async Task<int> CreateAsync(BoardTestRig rig, GroupDraft draft)
    {
        var (id, error) = await rig.GroupSetup.CreateAsync(draft, John, Cancel);
        Assert.Null(error);
        return id!.Value;
    }

    private static async Task<int> GroupIdAsync(BoardTestRig rig)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayGroups.Select(g => g.Id).SingleAsync(Cancel);
    }
}
