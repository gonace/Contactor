using System.Net;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Removing a board (<see cref="BoardRemoval"/>), with the first board (8 relays) and Barn (4 relays, 2 inputs) in
/// memory, each with a group, schedules, input actions, runs and events: everything of the board goes, in one
/// transaction, and nothing of the other; its relays are turned off first when it answers; the last board can go too.
/// </summary>
public sealed class BoardRemovalTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_DeleteEverythingAboutTheBoard_AndNothingOfAnother()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, 1);
        await SeedAsync(boards, boards.BarnId);
        var firstBefore = await CountAsync(boards, 1);

        // Board 1's input acting on one of Barn's relays goes with Barn; board 1's own actions stay.
        int crossId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var input = await db.Inputs.Where(i => i.BoardId == 1 && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel);
            var cross = new InputAction { InputId = input, RelayId = await RelayIdAsync(boards, boards.BarnId, 1) };
            db.InputActions.Add(cross);
            await db.SaveChangesAsync(Cancel);
            crossId = cross.Id;
        }

        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();
        var preview = await removal.PreviewAsync(boards.BarnId, Cancel);
        var error = await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        Assert.Equal(new BoardRemovalPreview(boards.BarnId, "Barn", 4, 1, 2, 3, HasContact: true), preview);
        Assert.Null(error);
        Assert.Equal(Counts.None, await CountAsync(boards, boards.BarnId));
        Assert.Equal(firstBefore, await CountAsync(boards, 1));
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.False(await db.Boards.AnyAsync(b => b.Id == boards.BarnId, Cancel));
            Assert.False(await db.InputActions.AnyAsync(a => a.Id == crossId, Cancel));
            Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT foreign_keys AS \"Value\" FROM pragma_foreign_keys").SingleAsync(Cancel));
            Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pragma_foreign_key_check").SingleAsync(Cancel));
        }

        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        Assert.Null(registry.Find(boards.BarnId));
        Assert.NotNull(registry.Find(1));
        Assert.Null(await removal.PreviewAsync(boards.BarnId, Cancel));

        // A second phone that pressed Remove too.
        Assert.Equal(removal.AlreadyRemoved, await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));
    }

    // A failure part-way (here the last delete, the board's row) leaves every row, and the board runs again in its
    // place in the list, with its saved model: it connects again and takes commands.
    [Fact]
    public async Task Should_LeaveEverything_AndRunTheBoardAgain_WhenTheDeleteFails()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, boards.BarnId);
        var shedId = await AddShedAsync(boards);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            // Barn's saved model and address: a VM201 on the simulator.
            await db.Boards.Where(b => b.Id == boards.BarnId).ExecuteUpdateAsync(
                b => b.SetProperty(x => x.Type, "vm201").SetProperty(x => x.Host, "127.0.0.1").SetProperty(x => x.Port, simulator.Port), Cancel);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER keep_board BEFORE DELETE ON \"Board\" BEGIN SELECT RAISE(ABORT, 'kept'); END;", Cancel);
        }

        var before = await CountAsync(boards, boards.BarnId);
        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();
        await Assert.ThrowsAnyAsync<Exception>(() => removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        // Its events too; the board running again may log more of its own meanwhile (its first status).
        var after = await CountAsync(boards, boards.BarnId);
        Assert.Equal(before with { Events = 0 }, after with { Events = 0 });
        Assert.True(after.Events >= before.Events);
        await using (var check = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.True(await check.Boards.AnyAsync(b => b.Id == boards.BarnId, Cancel));
            Assert.False(await check.EventLog.AnyAsync(e => e.Action == EventActions.BoardRemoved, Cancel));
        }

        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        int[] order = [1, boards.BarnId, shedId];
        Assert.Equal(order, registry.All.Select(b => b.BoardId));
        var barn = registry.Find(boards.BarnId)!;
        Assert.False(barn.IsClosed);
        await Wait.UntilAsync(() => barn.State.IsConnected);
        await boards.App.Services.GetRequiredService<RelayControl>().TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 1), John, Cancel);
        await Wait.UntilAsync(() => (simulator.Outputs & 1) == 1);
    }

    // Its relays go off first, a group's in its order; the live board is stopped and let go before the rows go, and the
    // other board's relays stay as they are.
    [Fact]
    public async Task Should_TurnOffItsRelaysFirst_ThenStopTheBoard_WhenItAnswers()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId, runOnSec: 0);
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(await RelayIdAsync(boards, 1, 2), John, Cancel);
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);
        var sequencer = boards.App.Services.GetRequiredService<GroupSequencer>();
        await sequencer.StartGroupAsync(groupId, John, Cancel);
        await Wait.UntilAsync(() => boards.Barn.State.IsOn(3) && boards.Barn.State.IsOn(4));
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        var barn = registry.Find(boards.BarnId)!;
        var changes = 0;
        registry.BoardsChanged += (_, _) => changes++;

        var error = await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        Assert.Null(error);
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
        Assert.True(boards.First.State.IsOn(2));

        // Heat (relay 4) before Fan (relay 3).
        var offs = boards.Barn.Sent.Where(s => !s.On).Select(s => s.Relays).ToList();
        Assert.True(offs.FindIndex(m => m.Contains(4)) < offs.FindIndex(m => m.Contains(3)));

        // Stopped and let go: no watch, no listener, no part of a safety service left for it.
        Assert.Null(registry.Find(boards.BarnId));
        Assert.Equal(1, changes);
        Assert.False(boards.Barn.HasListeners);
        Assert.Null(sequencer.For(boards.BarnId));
        Assert.Equal(0, barn.Capabilities.RelayCount);
        await Assert.ThrowsAnyAsync<BoardNotConnectedException>(() => barn.TurnOnAsync(ChannelMask.Of(1), Cancel));
    }

    // While it waits for the board to show every relay off, "All off" goes again for what is still on, but only the
    // first one logs an "Off": here relay 1 comes on again at the board twice, and is turned off three times.
    [Fact]
    public async Task Should_LogOnlyTheFirstAllOff_WhileTheBoardIsBeingRemoved()
    {
        await using var boards = await TwoBoards.StartAsync();
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 1), John, Cancel);
        await control.TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 2), John, Cancel);
        var offs = 0;
        int? loggedBeforeTheThird = null;
        boards.Barn.AfterSwitch = async () =>
        {
            if (boards.Barn.Sent[^1].On)
            {
                return;
            }

            offs++;
            if (offs < 3)
            {
                // On again at the board itself, as a relay that won't stay off.
                boards.Barn.SetRelay(1, on: true);
                return;
            }

            if (offs == 3)
            {
                await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
                loggedBeforeTheThird = await db.EventLog.CountAsync(e => e.BoardId == boards.BarnId && e.Action == EventActions.Off && e.Actor == John, Cancel);
            }
        };

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        Assert.Equal(3, offs);
        Assert.Equal(2, loggedBeforeTheThird);
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
    }

    // From the confirm on, nothing turns one of its relays on: a tap, an input's action or a group's start is refused,
    // while its OFFs go through.
    [Fact]
    public async Task Should_RefuseEveryOn_WhileTheBoardIsBeingRemoved()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId, runOnSec: 0);
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        var sequencer = boards.App.Services.GetRequiredService<GroupSequencer>();
        var relay = await RelayIdAsync(boards, boards.BarnId, 1);
        await control.TurnOnAsync(relay, John, Cancel);
        List<Exception> refused = [];
        var tried = false;
        boards.Barn.AfterSwitch = async () =>
        {
            if (tried)
            {
                return;
            }

            tried = true;
            foreach (var on in new Func<Task>[]
            {
                () => control.TurnOnAsync(relay, John, Cancel),
                () => control.TurnOnAsync(relay, EventActors.ForInput(1), Cancel),
                () => sequencer.StartGroupAsync(groupId, John, Cancel),
            })
            {
                refused.Add(await Assert.ThrowsAsync<BoardClosedException>(on));
            }
        };

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        Assert.Equal(3, refused.Count);
        Assert.Contains(refused, e => e.Message == LiveBoard.BeingRemoved);
        Assert.Single(boards.Barn.Sent, s => s.On);
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);

        // After it is gone, a command for one of its relays fails as one for a relay there isn't.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => control.TurnOffAsync(relay, John, Cancel));
    }

    // A group still running on stops the removal: nothing is removed, the board works as before, and the person is told
    // when the run-on ends.
    [Fact]
    public async Task Should_RemoveNothing_WhileAGroupRunsOn()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId);
        var sequencer = boards.App.Services.GetRequiredService<GroupSequencer>();
        await sequencer.StartGroupAsync(groupId, John, Cancel);
        await Wait.UntilAsync(() => boards.Barn.State.IsOn(3) && boards.Barn.State.IsOn(4));
        var before = await CountAsync(boards, boards.BarnId);
        var clock = boards.App.Services.GetRequiredService<LocalClock>();
        var asked = clock.UtcNow;

        var error = await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        // The fan runs on 10 minutes from the heat's confirmed OFF, or from the removal's look before it was confirmed.
        var ends = await RunOnEndsAsync(boards, groupId);
        Assert.Contains(error, Untils(clock, asked.AddSeconds(600), ends).Select(u => $"Heater runs on until {u}. Remove Barn when it's done."));
        Assert.True(boards.Barn.State.IsOn(3));
        Assert.False(boards.Barn.State.IsOn(4));
        Assert.Equal(before with { Events = 0 }, await CountAsync(boards, boards.BarnId) with { Events = 0 });
        var barn = boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId)!;
        Assert.False(barn.IsClosed);
        await boards.App.Services.GetRequiredService<RelayControl>().TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 1), John, Cancel);
        Assert.True(boards.Barn.State.IsOn(1));
    }

    // A trip while the board is closed: the stop's run-on (the fan) still comes on, so the board isn't removed with the
    // heater hot, and the removal is called off with the run-on's end.
    [Fact]
    public async Task Should_LetATripsRunOnThrough_AndRemoveNothing_WhileTheBoardIsClosed()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId);
        await boards.App.Services.GetRequiredService<GroupSequencer>().StartGroupAsync(groupId, John, Cancel);
        await Wait.UntilAsync(() => boards.Barn.State.IsOn(3) && boards.Barn.State.IsOn(4));
        var barn = boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId)!;
        await barn.CloseAsync();

        // The fan goes off at the board: the heat goes off, and the fan comes on again to run on.
        boards.Barn.SetRelay(3, on: false);
        await Wait.UntilAsync(() => boards.Barn.State.IsOn(3) && !boards.Barn.State.IsOn(4));

        var error = await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        Assert.Matches("^Heater runs on until \\d\\d:\\d\\d\\. Remove Barn when it's done\\.$", error);
        Assert.True(boards.Barn.State.IsOn(3));
        Assert.False(barn.IsClosed);
        Assert.NotNull(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
    }

    // With three relays, the message says when the whole group is off: the run-on under way and every one above it.
    [Fact]
    public async Task Should_SayWhenTheWholeGroupIsOff()
    {
        await using var boards = await TwoBoards.StartAsync();
        int groupId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var relays = await db.Relays.Where(r => r.BoardId == boards.BarnId).ToDictionaryAsync(r => r.Channel, r => r.Id, Cancel);
            var group = new RelayGroup { BoardId = boards.BarnId, Name = "Booth", SortOrder = 1 };
            group.AddStep("Fan", relays[1], startDelaySec: 1, runOnSec: 600);
            group.AddStep("Pump", relays[2], startDelaySec: 1, runOnSec: 300);
            group.AddStep("Heat", relays[3], startDelaySec: 1);
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            groupId = group.Id;
        }

        await boards.App.Services.GetRequiredService<GroupSequencer>().StartGroupAsync(groupId, John, Cancel);
        await Wait.UntilAsync(() => boards.Barn.State.IsOn(1) && boards.Barn.State.IsOn(2) && boards.Barn.State.IsOn(3));
        var clock = boards.App.Services.GetRequiredService<LocalClock>();
        var asked = clock.UtcNow;

        var error = await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        // The pump runs on 5 minutes, then the fan 10 more: counted from the heat's confirmed OFF, or from the
        // removal's look before it was confirmed.
        var pumpOff = await RunOnEndsAsync(boards, groupId);
        Assert.Contains(error, Untils(clock, asked.AddSeconds(300 + 600), pumpOff.AddSeconds(600)).Select(u => $"Booth runs on until {u}. Remove Barn when it's done."));
    }

    // An ON already on its way when the board closes lands before the removal looks at the board, and is turned off.
    [Fact]
    public async Task Should_WaitForAnOnUnderWay_BeforeLookingAtTheBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boards.Barn.BeforeOn = async () =>
        {
            held.TrySetResult();
            await hold.Task;
        };
        var on = boards.App.Services.GetRequiredService<RelayControl>().TurnOnAsync(await RelayIdAsync(boards, boards.BarnId, 1), John, Cancel);
        await held.Task;

        // The ON saves its run after it reached the board. The removal's first OFF waits for that, so the board's rows
        // are still there when it does: otherwise the ON fails as one for a board that was removed meanwhile.
        boards.Barn.AfterOff = () => on;

        var barn = boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId)!;
        var removing = boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);
        await Wait.UntilAsync(() => barn.IsClosed);

        hold.SetResult();
        await on;
        Assert.Null(await removing);

        // The held ON reached the board before the removal's first OFF, which then turned it off.
        var sent = boards.Barn.Sent;
        Assert.True(sent.ToList().FindIndex(s => s.On && s.Relays.Contains(1)) < sent.ToList().FindIndex(s => !s.On));
        Assert.Equal(ChannelMask.None, boards.Barn.State.Relays);
        Assert.Null(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
    }

    // Without contact nothing can be sent: removing is still allowed when the person was told so, and nothing is sent.
    [Fact]
    public async Task Should_RemoveAsItIs_WithoutContact_OnlyWhenThePersonWasToldSo()
    {
        var lost = FakeRelayBoard.LostContact(relayCount: 4, inputCount: 2);
        await using var boards = await TwoBoards.StartAsync(lost);
        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();
        Assert.False((await removal.PreviewAsync(boards.BarnId, Cancel))!.HasContact);

        // Told its relays would go off first: it lost contact since, so nothing is removed.
        Assert.Equal(
            "Barn has lost contact, so its relays couldn't be turned off. Nothing was removed.",
            await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        Assert.False(registry.Find(boards.BarnId)!.IsClosed);

        Assert.Null(await removal.RemoveAsync(boards.BarnId, turnOffFirst: false, John, Cancel));
        Assert.Empty(lost.Sent);
        Assert.Null(registry.Find(boards.BarnId));
    }

    // Who removed it stays on Events, with no board, after the board's own events are gone: its name and model as they
    // were, since its row is gone.
    [Fact]
    public async Task Should_LogWhoRemovedTheBoard_InAnEventThatStays()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, boards.BarnId);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Boards.Where(b => b.Id == boards.BarnId).ExecuteUpdateAsync(b => b.SetProperty(x => x.Type, "vm204"), Cancel);
        }

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var removed = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardRemoved, Cancel);
            Assert.Equal((null, null, null, null, John), (removed.BoardId, removed.RelayId, removed.GroupId, removed.InputId, removed.Actor));
            Assert.Equal("""{"name":"Barn","model":"Velleman VM204"}""", removed.Details);
        }

        var rows = await boards.App.Services.GetRequiredService<EventsHandler>().GetAsync(EventFilter.All, null, 50, Cancel);
        var row = Assert.Single(rows, r => r.View.What.StartsWith("removed relay board", StringComparison.Ordinal));
        Assert.Equal(("removed relay board Barn (Velleman VM204)", true, null), (row.View.What, row.View.IsPerson, row.Board));
        Assert.Contains(row, await boards.App.Services.GetRequiredService<EventsHandler>().GetAsync(EventFilter.People, null, 50, Cancel, boards: [1]));
    }

    // The deletes and the removal's event are saved together: an event that can't be saved leaves every row of the board.
    [Fact]
    public async Task Should_RemoveNothing_WhenTheRemovalsEventCantBeSaved()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, boards.BarnId);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER no_removal BEFORE INSERT ON \"EventLog\" WHEN NEW.\"Action\" = 'board-removed' BEGIN SELECT RAISE(ABORT, 'x'); END;", Cancel);
        }

        var before = await CountAsync(boards, boards.BarnId);
        await Assert.ThrowsAnyAsync<Exception>(() => boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        // Its events too; the board running again may log more of its own meanwhile.
        var after = await CountAsync(boards, boards.BarnId);
        Assert.Equal(before with { Events = 0 }, after with { Events = 0 });
        Assert.True(after.Events >= before.Events);
        await using (var check = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.True(await check.Boards.AnyAsync(b => b.Id == boards.BarnId, Cancel));
        }

        Assert.NotNull(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
    }

    // The event names the board as it is at the delete, after its relays are off: here renamed meanwhile.
    [Fact]
    public async Task Should_NameTheBoard_AsItIsAtTheDelete()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        registry.BoardsChanged += (_, _) =>
        {
            using var db = boards.Factory.CreateDbContext();
            db.Boards.Where(b => b.Id == boards.BarnId).ExecuteUpdate(b => b.SetProperty(x => x.Name, "Garage"));
        };

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        await using var check = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal("""{"name":"Garage"}""", (await check.EventLog.SingleAsync(e => e.Action == EventActions.BoardRemoved, Cancel)).Details);
    }

    // Events that stay (from another board, or about no board) and name a removed board's relay, group or input by id
    // never show the id: the name the event saved, otherwise plain words. Those that still resolve are as before.
    [Fact]
    public async Task Should_ShowARemovedBoardsRelayGroupAndInputPlainly()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId);
        var barnRelay = await RelayIdAsync(boards, boards.BarnId, 2);
        var firstRelay = await RelayIdAsync(boards, 1, 2);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var barnInput = await db.Inputs.Where(i => i.BoardId == boards.BarnId && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel);
            var at = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            db.EventLog.AddRange(
                new EventLogEntry { TimestampUtc = at, BoardId = 1, RelayId = barnRelay, Actor = EventActors.ForInput(barnInput), Action = EventActions.On },
                new EventLogEntry { TimestampUtc = at.AddSeconds(1), GroupId = groupId, Actor = John, Action = EventActions.GroupOn },
                new EventLogEntry { TimestampUtc = at.AddSeconds(2), GroupId = groupId, Actor = John, Action = EventActions.GroupDelete, Details = """{"name":"Heater"}""" },
                new EventLogEntry { TimestampUtc = at.AddSeconds(3), InputId = barnInput, Actor = EventActors.External, Action = EventActions.InputChanged, Details = """{"on":true}""" },
                new EventLogEntry { TimestampUtc = at.AddSeconds(4), BoardId = 1, RelayId = firstRelay, Actor = John, Action = EventActions.On });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        var rows = await boards.App.Services.GetRequiredService<EventsHandler>().GetAsync(EventFilter.All, null, 50, Cancel);
        var views = rows.Select(r => (r.View.Who, r.View.What, r.View.Target)).ToList();
        Assert.Contains(("A removed input", "turned on", "a removed relay"), views);
        Assert.Contains(("A removed input:", "Closed", null), views);
        Assert.Contains(views, v => v.What == "turned on the group" && v.Target == "a removed group");
        Assert.Contains(views, v => v.What == "deleted the group" && v.Target == "Heater");
        Assert.Contains(views, v => v.What == "turned on" && v.Target == "Relay 2");
    }

    // A relay and a group that a removed board's input turned on (their owner "input:n" with no input left), and a
    // relay it turned off: the Relays, Relay and Group screens say "a removed input" and don't break.
    [Fact]
    public async Task Should_ShowARemovedInput_AsWhoSwitchedARelay()
    {
        await using var boards = await TwoBoards.StartAsync();
        var on = await RelayIdAsync(boards, 1, 2);
        var off = await RelayIdAsync(boards, 1, 3);
        int groupId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Heater", SortOrder = 1 };
            group.AddStep("Fan", await RelayIdAsync(boards, 1, 5), startDelaySec: 1);
            group.AddStep("Heat", await RelayIdAsync(boards, 1, 6), startDelaySec: 1);
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            groupId = group.Id;
        }

        boards.First.SetRelay(2, true);
        await boards.App.Services.GetRequiredService<GroupSequencer>().StartGroupAsync(groupId, John, Cancel);
        await Wait.UntilAsync(() =>
        {
            using var check = boards.Factory.CreateDbContext();
            return check.RelayRuntimes.Any(r => r.RelayId == on && r.OnSinceUtc != null)
                && check.GroupRuntimes.Any(r => r.GroupId == groupId && r.Phase == GroupPhases.Idle && r.OnSinceUtc != null);
        });
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var input = EventActors.ForInput(await db.Inputs.Where(i => i.BoardId == boards.BarnId && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel));
            await db.RelayRuntimes.Where(r => r.RelayId == on).ExecuteUpdateAsync(r => r.SetProperty(x => x.TurnedOnBy, input), Cancel);
            await db.GroupRuntimes.Where(r => r.GroupId == groupId).ExecuteUpdateAsync(r => r.SetProperty(x => x.TurnedOnBy, input), Cancel);
            db.EventLog.Add(new EventLogEntry { TimestampUtc = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), BoardId = 1, RelayId = off, Actor = input, Action = EventActions.Off });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        using (var relays = await PageRenderer.StartAsync<Contactor.Web.Components.Relays>(boards.App.Services, John, "John"))
        {
            var text = await relays.TextAsync();
            Assert.Empty(relays.Errors);

            // The relay, the one turned off, and the group.
            Assert.Equal(3, text.Split("· a removed input").Length - 1);
        }

        using (var relay = await PageRenderer.StartAsync<Contactor.Web.Components.RelayDetail>(
            boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = on }))
        {
            Assert.Empty(relay.Errors);
            Assert.Contains(", by a removed input.", await relay.TextAsync(), StringComparison.Ordinal);
        }

        using var groupPage = await PageRenderer.StartAsync<Contactor.Web.Components.GroupDetail>(
            boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = groupId });
        Assert.Empty(groupPage.Errors);
        Assert.Contains("· a removed input", await groupPage.TextAsync(), StringComparison.Ordinal);
    }

    // The last board can be removed too, as any other: its relays turned off first, everything about it deleted.
    [Fact]
    public async Task Should_RemoveTheLastBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, 1);
        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();
        Assert.Null(await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));
        await boards.App.Services.GetRequiredService<RelayControl>().TurnOnAsync(await RelayIdAsync(boards, 1, 6), John, Cancel);

        Assert.Null(await removal.RemoveAsync(1, turnOffFirst: true, John, Cancel));

        Assert.False(boards.First.State.IsOn(6));
        Assert.Empty(boards.App.Services.GetRequiredService<BoardRegistry>().All);
        Assert.Equal(Counts.None, await CountAsync(boards, 1));
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.AnyAsync(Cancel));
        Assert.True(await db.Installations.AnyAsync(i => i.SetupDoneUtc != null, Cancel));
    }

    // Should the app run no board at all (a new installation, or later every board removed), the screens say so.
    [Fact]
    public async Task Should_KeepWorking_WithNoBoard()
    {
        await using var boards = await TwoBoards.StartAsync();
        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        await registry.RemoveAsync(boards.BarnId, Cancel);
        await registry.RemoveAsync(1, Cancel);

        var control = boards.App.Services.GetRequiredService<RelayControl>();
        Assert.Empty(await control.TurnOffAllBoardsAsync(John, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => control.TurnAllOffAsync(1, John, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => control.TurnOnAsync(1, John, Cancel));
        using var http = boards.App.CreateClient();
        foreach (var page in new[] { "/", "/events", "/settings", "/debug", "/group/new", "/board/1" })
        {
            var response = await http.GetAsync(page, Cancel);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page}: {response.StatusCode}");
        }

        Assert.Contains("There's no relay board in Contactor.", WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel)), StringComparison.Ordinal);
        Assert.Contains("There's no relay board in Contactor.", WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel)), StringComparison.Ordinal);
    }

    // The settings' address and login are the first board's: the board that becomes first never takes
    // them, which were the removed board's, whatever its model, or none.
    [Theory]
    [InlineData("", "vm201")]
    [InlineData("vm204", "vm204")]
    public async Task Should_KeepTheNextBoardWithoutAnAddress_WhenTheFirstIsRemoved(string type, string portOf)
    {
        await using var boards = await TwoBoards.StartAsync();
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Boards.Where(b => b.Id == boards.BarnId).ExecuteUpdateAsync(b => b.SetProperty(x => x.Type, type), Cancel);
        }

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(1, turnOffFirst: true, John, Cancel));

        await using var check = await boards.Factory.CreateDbContextAsync(Cancel);
        var barn = await check.Boards.SingleAsync(Cancel);
        Assert.Equal((boards.BarnId, "", BoardCatalogue.Find(portOf)!.Model.DefaultPort, "", ""), (barn.Id, barn.Host, barn.Port, barn.Username, barn.Password));
        Assert.False(BoardSetup.SavedEndpoint(barn)!.HasHost);
    }

    // A third board after Barn, in memory: Shed (2 relays).
    private static async Task<int> AddShedAsync(TwoBoards boards)
    {
        int id;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var row = new Board { Name = "Shed", SortOrder = 3 };
            db.Boards.Add(row);
            await db.SaveChangesAsync(Cancel);
            id = row.Id;
        }

        await boards.App.Services.SeedBoardAsync(id, 2, 0, Cancel);
        await boards.App.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(2).Build(id), Cancel);
        return id;
    }

    // A group of relays 3 → 4 (Fan → Heat, Fan running on for 10 minutes), schedules for relay 2 and the group, and
    // input 1's actions on relay 2 and the group, with a run and an event: everything removing the board deletes.
    private static async Task<int> SeedAsync(TwoBoards boards, int boardId, int runOnSec = 600)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var relays = await db.Relays.Where(r => r.BoardId == boardId).ToDictionaryAsync(r => r.Channel, r => r.Id, Cancel);
        var input = await db.Inputs.Where(i => i.BoardId == boardId && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel);
        var group = new RelayGroup { BoardId = boardId, Name = "Heater", SortOrder = 1 };
        group.AddStep("Fan", relays[3], startDelaySec: 1, runOnSec: runOnSec);
        group.AddStep("Heat", relays[4], startDelaySec: 1);
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.Schedules.Add(new Schedule { RelayId = relays[2], Days = ScheduleDays.All, OnTime = new(6, 0), OffTime = new(7, 0), CreatedBy = John });
        db.Schedules.Add(new Schedule { GroupId = group.Id, Days = ScheduleDays.All, OnTime = new(6, 0), OffTime = new(7, 0), CreatedBy = John });
        db.InputActions.Add(new InputAction { InputId = input, RelayId = relays[2] });
        db.InputActions.Add(new InputAction { InputId = input, GroupId = group.Id });
        db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, BoardId = boardId, RelayId = relays[1], Actor = John, Action = EventActions.Rename });
        await db.SaveChangesAsync(Cancel);

        // The group's runtime row comes from its board's sequencer, at its next step.
        await Wait.UntilAsync(() =>
        {
            using var check = boards.Factory.CreateDbContext();
            return check.GroupRuntimes.Any(r => r.GroupId == group.Id);
        });
        return group.Id;
    }

    // When the group's run-on under way ends. The sequencer saves it when the board confirms the stop's OFF, on its own
    // step, which can come after the removal has answered.
    private static async Task<DateTime> RunOnEndsAsync(TwoBoards boards, int groupId)
    {
        DateTime? next = null;
        await Wait.UntilAsync(() =>
        {
            using var db = boards.Factory.CreateDbContext();
            next = db.GroupRuntimes.AsNoTracking().Single(r => r.GroupId == groupId).NextStepAtUtc;
            return next is not null;
        });
        return next!.Value;
    }

    // Every "HH:mm" the removal can say for a run-on that ends between from and to, both included.
    private static List<string> Untils(LocalClock clock, DateTime from, DateTime to)
    {
        Assert.True(from <= to, $"{from:O} is after {to:O}.");
        List<string> untils = [];
        for (var at = from; at < to.AddMinutes(1); at = at.AddMinutes(1))
        {
            untils.Add(clock.ToLocal(Min(at, to)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        }

        return untils;
    }

    private static DateTime Min(DateTime a, DateTime b)
    {
        return a < b ? a : b;
    }

    private static async Task<Counts> CountAsync(TwoBoards boards, int boardId)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var relayIds = await db.Relays.Where(r => r.BoardId == boardId).Select(r => r.Id).ToListAsync(Cancel);
        var groupIds = await db.RelayGroups.Where(g => g.BoardId == boardId).Select(g => g.Id).ToListAsync(Cancel);
        var inputIds = await db.Inputs.Where(i => i.BoardId == boardId).Select(i => i.Id).ToListAsync(Cancel);
        return new Counts(
            relayIds.Count,
            await db.RelayRuntimes.CountAsync(r => relayIds.Contains(r.RelayId), Cancel),
            inputIds.Count,
            groupIds.Count,
            await db.RelayGroupMembers.CountAsync(m => groupIds.Contains(m.GroupId) || relayIds.Contains(m.RelayId), Cancel),
            await db.GroupRuntimes.CountAsync(r => groupIds.Contains(r.GroupId), Cancel),
            await db.Schedules.CountAsync(s => (s.RelayId != null && relayIds.Contains(s.RelayId.Value)) || (s.GroupId != null && groupIds.Contains(s.GroupId.Value)), Cancel),
            await db.InputActions.CountAsync(a => inputIds.Contains(a.InputId) || (a.RelayId != null && relayIds.Contains(a.RelayId.Value)) || (a.GroupId != null && groupIds.Contains(a.GroupId.Value)), Cancel),
            await db.EventLog.CountAsync(e => e.BoardId == boardId, Cancel),
            await db.BoardRuntimes.CountAsync(r => r.BoardId == boardId, Cancel));
    }

    private static async Task<int> RelayIdAsync(TwoBoards boards, int boardId, int channel)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }

    private sealed record Counts(int Relays, int RelayRuntimes, int Inputs, int Groups, int Members, int GroupRuntimes, int Schedules, int InputActions, int Events, int BoardRuntimes)
    {
        public static Counts None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
