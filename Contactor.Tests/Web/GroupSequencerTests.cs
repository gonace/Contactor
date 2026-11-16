using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.Events;
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
    public async Task Should_RefuseARelayOutsideTheGroup()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await CreateAsync(rig, FanHeater);

        Assert.Equal(
            "That relay isn't in the group.",
            await rig.Groups.SetMemberAsync(await GroupIdAsync(rig), 5, on: true, John, Cancel));
        Assert.False(rig.Client.State.IsOn(5));
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
        Assert.Equal("Already in Fan heater – Lathe", TestTexts.GroupPlan.Note(relays[0]));
        Assert.Equal("Has its own schedules", TestTexts.GroupPlan.Note(relays[3]));
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
        Assert.Equal([2, 1], await db.RelayGroupMembers.OrderBy(m => m.Step!.Position).Select(m => m.RelayId).ToListAsync(Cancel));
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
            runtime.PendingStep = 1;
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

        foreach (var step in new[]
        {
            new RelayGroupStep { GroupId = id, Position = 2, Name = "Same step" },
            new RelayGroupStep { GroupId = id, Position = 3, Name = "A", StartDelaySec = 0 },
            new RelayGroupStep { GroupId = id, Position = 3, Name = "A", RunOnSec = 1801 },
            new RelayGroupStep { GroupId = id, Position = 65, Name = "A" },
        })
        {
            await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
            db.RelayGroupSteps.Add(step);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        }

        // A relay's place in its step, and its step in its own group.
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var fan = await db.RelayGroupSteps.SingleAsync(s => s.GroupId == id && s.Position == 1, Cancel);
            db.RelayGroupMembers.Add(new RelayGroupMember { GroupId = id, StepId = fan.Id, RelayId = 3, Order = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        }

        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var fan = await db.RelayGroupSteps.SingleAsync(s => s.GroupId == id && s.Position == 1, Cancel);
            db.RelayGroupMembers.Add(new RelayGroupMember { GroupId = id, StepId = fan.Id, RelayId = 3, Order = 65 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        }

        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var other = new RelayGroup { BoardId = 1, Name = "Annan", Icon = GroupIcons.Bolt };
            other.AddStep("A", 4);
            other.AddStep("B", 5);
            db.RelayGroups.Add(other);
            await db.SaveChangesAsync(Cancel);
            var fan = await db.RelayGroupSteps.SingleAsync(s => s.GroupId == id && s.Position == 1, Cancel);
            db.RelayGroupMembers.Add(new RelayGroupMember { GroupId = other.Id, StepId = fan.Id, RelayId = 3, Order = 2 });
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
        other.AddStep("A", 2);
        other.AddStep("B", 3);
        db.RelayGroups.Add(other);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
    }

    // Two fans in one step, then two heaters in the next.
    private static GroupDraft FansHeaters => new(
        "Workshop heat",
        GroupIcons.Fan,
        120,
        [new GroupStepDraft("Fans", [1, 2], 3, 180), new GroupStepDraft("Heaters", [3, 4], 3, 0)]);

    [Fact]
    public async Task Should_SwitchTheRelaysOfAStepTogether_OnTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FansHeaters);

        await rig.Groups.StartGroupAsync(id, John, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1) && rig.Client.State.IsOn(2));
        Assert.False(rig.Client.State.IsOn(3) || rig.Client.State.IsOn(4));
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += TimeSpan.FromSeconds(3);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b1111);

        // One heater by hand: the other stays on, the fans too.
        Assert.Null(await rig.Groups.SetMemberAsync(id, 3, on: false, John, Cancel));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b1011);

        // A fan stopped at the board: both heaters off at once, and the fan back on to run on.
        await rig.Simulator.SetOutputsAsync((byte)(Vm201Channel.Mask(1) | Vm201Channel.Mask(4)));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b1001);
        await rig.Groups.StepAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b0011);
        var trip = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
        Assert.Equal("""{"missing":"Relay 2","off":["Relay 4"]}""", trip.Details);
    }

    [Fact]
    public async Task Should_RefuseAHeater_UntilBothFansAreOn_OnTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FansHeaters);
        Assert.Null(await rig.Groups.SetMemberAsync(id, 1, on: true, John, Cancel));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));

        Assert.Equal("Needs Fans to be on.", await rig.Groups.SetMemberAsync(id, 3, on: true, John, Cancel));
        Assert.False(rig.Client.State.IsOn(3));
    }

    // Issue #353: Fan 1 off; Fan 2 off half a second later; Fan 1 on again. The board's status shows Fan 2's OFF before
    // Fan 1's ON: the stop waits for that ON, so the run stays John's, with no "outside the app" in between.
    [Fact]
    public async Task Should_KeepTheRun_OfAFanTurnedOnAgain_WhenTheBoardShowsTheOthersOffFirst()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        int Fan(int channel) => rig.SecondRelayId(channel);
        var id = await CreateAsync(rig, new GroupDraft("Barn fans", GroupIcons.Fan, 120, [new GroupStepDraft("Fans", [Fan(1), Fan(2)], 3, 180)]));
        await rig.Groups.StartGroupAsync(id, John, Cancel);
        await rig.Groups.StepAsync(Cancel);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(1), on: false, John, Cancel));
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal((GroupPhases.Idle, ChannelMask.Of(2)), ((await RuntimeAsync(rig, id)).Phase, board.State.Relays));

        board.HoldStatus = true;
        rig.Time.Now += TimeSpan.FromSeconds(0.5);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(2), on: false, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.5);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(1), on: true, John, Cancel));

        // The board shows nothing on: Fan 2's OFF, with Fan 1's ON still to come.
        rig.Time.Now += TimeSpan.FromSeconds(0.2);
        board.SetRelay(2, on: false);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(GroupPhases.Stopping, (await RuntimeAsync(rig, id)).Phase);

        board.ReleaseStatus();
        await rig.Groups.StepAsync(Cancel);

        var runtime = await RuntimeAsync(rig, id);
        Assert.Equal((ChannelMask.Of(1), GroupPhases.Idle, John), (board.State.Relays, runtime.Phase, runtime.TurnedOnBy));
        Assert.Equal(BoardTestRig.Start, runtime.OnSinceUtc);
        Assert.Contains(await rig.EventsAsync(), e => e.Action == EventActions.On && e.RelayId == Fan(1) && e.Actor == John);
    }

    // Issue #398: as above, and "Turn off" comes while Fan 1's ON is still on its way. Its OFF goes after that ON, and
    // the group ends with every relay off and no run.
    [Fact]
    public async Task Should_TurnEveryFanOff_WhenTheGroupIsTurnedOffWhileAFansOnIsOnItsWay()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        int Fan(int channel) => rig.SecondRelayId(channel);
        var id = await CreateAsync(rig, new GroupDraft("Barn fans", GroupIcons.Fan, 120, [new GroupStepDraft("Fans", [Fan(1), Fan(2)], 3, 180)]));
        await rig.Groups.StartGroupAsync(id, John, Cancel);
        await rig.Groups.StepAsync(Cancel);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(1), on: false, John, Cancel));
        await rig.Groups.StepAsync(Cancel);

        board.HoldStatus = true;
        rig.Time.Now += TimeSpan.FromSeconds(0.5);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(2), on: false, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.5);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Fan(1), on: true, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.2);
        board.SetRelay(2, on: false);
        await rig.Groups.StepAsync(Cancel);

        var sent = board.Sent.Count;
        await rig.Groups.StopGroupAsync(id, John, Cancel);
        Assert.Equal([(ChannelMask.Of(1), false)], board.Sent.Skip(sent));
        Assert.Equal(GroupPhases.Stopping, (await RuntimeAsync(rig, id)).Phase);

        // The board shows Fan 1 off: its ON and then its OFF. The ON never showed, so the stop is done once it is no
        // longer on its way.
        board.ReleaseStatus();
        await rig.Groups.StepAsync(Cancel);
        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        var runtime = await RuntimeAsync(rig, id);
        Assert.Equal((ChannelMask.None, GroupPhases.Idle, (string?)null), (board.State.Relays, runtime.Phase, runtime.TurnedOnBy));
    }

    // Issue #406: Heater 1 is turned on, and Fan 1 off while that ON is still on its way. The heater's OFF goes first,
    // after its ON; Fan 1 runs on once the board shows the heater off, then stops. The interlock never trips.
    [Fact]
    public async Task Should_TurnAHeaterOffFirst_WhenAFanIsTurnedOffWhileTheHeatersOnIsOnItsWay()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        int Relay(int channel) => rig.SecondRelayId(channel);
        var id = await CreateAsync(rig, FansHeaters with { Steps = [new GroupStepDraft("Fans", [Relay(1), Relay(2)], 3, 180), new GroupStepDraft("Heaters", [Relay(3), Relay(4)], 3, 0)] });
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(1), on: true, John, Cancel));
        await rig.Groups.StepAsync(Cancel);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(2), on: true, John, Cancel));
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 2]), board.State.Relays);

        board.HoldStatus = true;
        rig.Time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(3), on: true, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.2);
        var sent = board.Sent.Count;
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(1), on: false, John, Cancel));
        Assert.Equal([(ChannelMask.Of(3), false)], board.Sent.Skip(sent));

        // The board shows the heater's ON and then its OFF: Fan 1 runs on once that ON is no longer on its way.
        board.ReleaseStatus();
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 2]), board.State.Relays);
        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 2]), board.State.Relays);

        rig.Time.Now += TimeSpan.FromSeconds(180);
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        var runtime = await RuntimeAsync(rig, id);
        Assert.Equal((ChannelMask.Of(2), GroupPhases.Idle), (board.State.Relays, runtime.Phase));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
    }

    // Issue #412, a lower step: Heater 1 is turned on, and off again before the board shows its ON. Its OFF goes after
    // that ON, and the stop is done only once the board shows it off with that ON no longer on its way. The fans stay on.
    [Fact]
    public async Task Should_TurnAHeaterOff_WhenItsOwnOnIsOnItsWay()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        int Relay(int channel) => rig.SecondRelayId(channel);
        var id = await CreateAsync(rig, FansHeaters with { Steps = [new GroupStepDraft("Fans", [Relay(1), Relay(2)], 3, 180), new GroupStepDraft("Heaters", [Relay(3), Relay(4)], 3, 0)] });
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(1), on: true, John, Cancel));
        await rig.Groups.StepAsync(Cancel);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(2), on: true, John, Cancel));
        await rig.Groups.StepAsync(Cancel);

        board.HoldStatus = true;
        rig.Time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(3), on: true, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.2);
        var sent = board.Sent.Count;
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(3), on: false, John, Cancel));
        Assert.Equal([(ChannelMask.Of(3), false)], board.Sent.Skip(sent));

        // The board shows the heater off, but its ON still counts as on its way: the stop waits for it.
        board.ReleaseStatus();
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal((ChannelMask.Of([1, 2]), GroupPhases.Stopping), (board.State.Relays, (await RuntimeAsync(rig, id)).Phase));

        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        var runtime = await RuntimeAsync(rig, id);
        Assert.Equal((ChannelMask.Of([1, 2]), GroupPhases.Idle), (board.State.Relays, runtime.Phase));
        Assert.Equal(sent + 1, board.Sent.Count);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
    }

    // Issue #412, the top step: Fan 2 is turned on, and off again before the board shows its ON. Its OFF goes at once,
    // after that ON, with nothing below on; Fan 1 stays on.
    [Fact]
    public async Task Should_TurnAFanOff_WhenItsOwnOnIsOnItsWay()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        int Relay(int channel) => rig.SecondRelayId(channel);
        var id = await CreateAsync(rig, FansHeaters with { Steps = [new GroupStepDraft("Fans", [Relay(1), Relay(2)], 3, 180), new GroupStepDraft("Heaters", [Relay(3), Relay(4)], 3, 0)] });
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(1), on: true, John, Cancel));
        await rig.Groups.StepAsync(Cancel);

        board.HoldStatus = true;
        rig.Time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(2), on: true, John, Cancel));
        rig.Time.Now += TimeSpan.FromSeconds(0.2);
        var sent = board.Sent.Count;
        Assert.Null(await rig.Groups.SetMemberAsync(id, Relay(2), on: false, John, Cancel));
        Assert.Equal([(ChannelMask.Of(2), false)], board.Sent.Skip(sent));

        board.ReleaseStatus();
        await rig.Groups.StepAsync(Cancel);
        Assert.Equal(GroupPhases.Stopping, (await RuntimeAsync(rig, id)).Phase);
        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        var runtime = await RuntimeAsync(rig, id);
        Assert.Equal((ChannelMask.Of(1), GroupPhases.Idle), (board.State.Relays, runtime.Phase));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.InterlockTrip);
    }

    [Fact]
    public async Task Should_SendTheFansOnceMore_WhenATripsOnWasLostInAReconnect()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FansHeaters);

        // A trip turned the heaters off and sent the fans' ON, which was lost with the connection.
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.GroupRuntimes.SingleAsync(r => r.GroupId == id, Cancel);
            (runtime.Phase, runtime.TargetLevel, runtime.PendingStep, runtime.PendingOn, runtime.PendingSinceUtc) =
                (GroupPhases.Stopping, 0, 1, true, BoardTestRig.Start);
            await db.SaveChangesAsync(Cancel);
        }

        _ = rig.Groups;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromMinutes(1);
        await rig.Groups.StepAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b0011);
    }

    // A trip was waiting for the middle step's ON when the connection dropped; meanwhile the relay above went off at the
    // board. Its ON isn't sent again below that gap.
    [Fact]
    public async Task Should_NotSendATripsLostOn_BelowAStepThatIsOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FansHeaters with { Steps = [new GroupStepDraft("A", [1]), new GroupStepDraft("B", [2, 3]), new GroupStepDraft("C", [4])] });
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.GroupRuntimes.SingleAsync(r => r.GroupId == id, Cancel);
            (runtime.Phase, runtime.TargetLevel, runtime.PendingStep, runtime.PendingOn, runtime.PendingSinceUtc) =
                (GroupPhases.Stopping, 0, 2, true, BoardTestRig.Start);
            await db.SaveChangesAsync(Cancel);
        }

        _ = rig.Groups;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromMinutes(1);
        await rig.Groups.StepAsync(Cancel);
        await rig.Groups.StepAsync(Cancel);

        Assert.Equal(0, rig.Simulator.Outputs);
    }

    [Fact]
    public async Task Should_SaveSteps_AndChangeThemOnlyWhileTheGroupIsOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await CreateAsync(rig, FansHeaters with { Steps = [new GroupStepDraft(" ", [1, 2]), new GroupStepDraft("Heaters", [3, 4])] });
        var view = (await rig.GroupSetup.GetAsync(id, Cancel))!;

        // A step without a name takes its first relay's.
        Assert.Equal(["Relay 1", "Heaters"], view.Steps.Select(s => s.Name));
        Assert.Equal([[1, 2], [3, 4]], view.Steps.Select(s => s.Relays.Select(r => r.Channel).ToArray()));
        var created = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.GroupCreate);
        Assert.Contains("\"steps\":[\"Relay 1\",\"Heaters\"]", created.Details, StringComparison.Ordinal);

        // A relay moved to another step is a change of order: only while off.
        var moved = FansHeaters with { Steps = [new GroupStepDraft("Fans", [1]), new GroupStepDraft("Heaters", [2, 3, 4])] };
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(1));
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
        Assert.StartsWith("Turn off the group", await rig.GroupSetup.SaveAsync(id, moved, John, Cancel), StringComparison.Ordinal);

        // Names and times can change while it runs.
        Assert.Null(await rig.GroupSetup.SaveAsync(id, FansHeaters with { Steps = [new GroupStepDraft("Fans", [1, 2], 3, 300), new GroupStepDraft("Heat", [3, 4])] }, John, Cancel));
        view = (await rig.GroupSetup.GetAsync(id, Cancel))!;
        Assert.Equal([("Fans", 300), ("Heat", 0)], view.Steps.Select(s => (s.Name, s.RunOnSec)));

        await rig.Simulator.SetOutputsAsync(0);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
        Assert.Null(await rig.GroupSetup.SaveAsync(id, moved, John, Cancel));
        view = (await rig.GroupSetup.GetAsync(id, Cancel))!;
        Assert.Equal([[1], [2, 3, 4]], view.Steps.Select(s => s.Relays.Select(r => r.Channel).ToArray()));
    }

    [Fact]
    public async Task Should_RefuseAStepWithoutRelays()
    {
        await using var rig = await BoardTestRig.StartAsync();

        Assert.Equal(
            "Each step needs at least one relay.",
            (await rig.GroupSetup.CreateAsync(FansHeaters with { Steps = [new GroupStepDraft("Fans", [1, 2]), new GroupStepDraft("Heaters", [])] }, John, Cancel)).Error);
        Assert.Equal(
            "A relay can only be in the group once.",
            (await rig.GroupSetup.CreateAsync(FansHeaters with { Steps = [new GroupStepDraft("Fans", [1, 2]), new GroupStepDraft("Heaters", [2, 3])] }, John, Cancel)).Error);
        Assert.Equal(
            "The step's name can be at most 40 characters.",
            (await rig.GroupSetup.CreateAsync(FansHeaters with { Steps = [new GroupStepDraft(new string('F', 41), [1, 2])] }, John, Cancel)).Error);

        // One step of two relays is a group.
        var (id, error) = await rig.GroupSetup.CreateAsync(FansHeaters with { Steps = [new GroupStepDraft("Fans", [1, 2])] }, John, Cancel);
        Assert.Null(error);
        Assert.NotNull(id);
    }

    private static async Task<int> CreateAsync(BoardTestRig rig, GroupDraft draft)
    {
        var (id, error) = await rig.GroupSetup.CreateAsync(draft, John, Cancel);
        Assert.Null(error);
        return id!.Value;
    }

    private static async Task<GroupRuntime> RuntimeAsync(BoardTestRig rig, int groupId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.GroupRuntimes.AsNoTracking().SingleAsync(r => r.GroupId == groupId, Cancel);
    }

    private static async Task<int> GroupIdAsync(BoardTestRig rig)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayGroups.Select(g => g.Id).SingleAsync(Cancel);
    }
}
