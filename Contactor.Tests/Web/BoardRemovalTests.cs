using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
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
        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();

        var preview = await removal.PreviewAsync(boards.BarnId, Cancel);
        var error = await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel);

        Assert.Equal(new BoardRemovalPreview(boards.BarnId, "Barn", 4, 1, 2, 2, HasContact: true), preview);
        Assert.Null(error);
        Assert.Equal(Counts.None, await CountAsync(boards, boards.BarnId));
        Assert.Equal(firstBefore, await CountAsync(boards, 1));
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Boards.AnyAsync(b => b.Id == boards.BarnId, Cancel));
        Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT foreign_keys AS \"Value\" FROM pragma_foreign_keys").SingleAsync(Cancel));
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pragma_foreign_key_check").SingleAsync(Cancel));

        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        Assert.Null(registry.Find(boards.BarnId));
        Assert.NotNull(registry.Find(1));
        Assert.Null(await removal.PreviewAsync(boards.BarnId, Cancel));
    }

    // A failure part-way (here the last delete, the board's row) leaves every row, and the board runs again.
    [Fact]
    public async Task Should_LeaveEverything_WhenTheDeleteFails()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, boards.BarnId);
        var before = await CountAsync(boards, boards.BarnId);
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER keep_board BEFORE DELETE ON \"Board\" BEGIN SELECT RAISE(ABORT, 'kept'); END;", Cancel);
        }

        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();
        await Assert.ThrowsAnyAsync<Exception>(() => removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));

        Assert.Equal(before, await CountAsync(boards, boards.BarnId));
        await using var check = await boards.Factory.CreateDbContextAsync(Cancel);
        Assert.True(await check.Boards.AnyAsync(b => b.Id == boards.BarnId, Cancel));
        Assert.NotNull(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
    }

    // Its relays go off first, a group's from the bottom without its run-on time; the live board is stopped and let go
    // before the rows go, and the other board's relays stay as they are.
    [Fact]
    public async Task Should_TurnOffItsRelaysFirst_ThenStopTheBoard_WhenItAnswers()
    {
        await using var boards = await TwoBoards.StartAsync();
        var groupId = await SeedAsync(boards, boards.BarnId);
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

        // Heat (relay 4) before Fan (relay 3), whose run-on of 10 minutes was cut short.
        var offs = boards.Barn.Sent.Where(s => !s.On).Select(s => s.Relays).ToList();
        Assert.True(offs.FindIndex(m => m.Contains(4)) < offs.FindIndex(m => m.Contains(3)));

        // Stopped and let go: no watch, no listener, no part of a safety service left for it.
        Assert.Null(registry.Find(boards.BarnId));
        Assert.Equal(1, changes);
        Assert.False(boards.Barn.HasListeners);
        Assert.Null(sequencer.For(boards.BarnId));
        Assert.Equal(0, barn.Capabilities.RelayCount);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => barn.TurnOnAsync(ChannelMask.Of(1), Cancel));
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
        Assert.NotNull(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));

        Assert.Null(await removal.RemoveAsync(boards.BarnId, turnOffFirst: false, John, Cancel));
        Assert.Empty(lost.Sent);
        Assert.Null(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
    }

    // The last board can go too: the app then has no board, as a new installation, and nothing throws.
    [Fact]
    public async Task Should_KeepWorking_WithNoBoard_AfterTheLastIsRemoved()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedAsync(boards, 1);
        var removal = boards.App.Services.GetRequiredService<BoardRemoval>();

        Assert.Null(await removal.RemoveAsync(boards.BarnId, turnOffFirst: true, John, Cancel));
        Assert.Null(await removal.RemoveAsync(1, turnOffFirst: true, John, Cancel));

        var registry = boards.App.Services.GetRequiredService<BoardRegistry>();
        Assert.Empty(registry.All);
        var control = boards.App.Services.GetRequiredService<RelayControl>();
        Assert.Empty(await control.TurnOffAllBoardsAsync(John, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => control.TurnAllOffAsync(1, John, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => registry.Only.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.False(await db.Boards.AnyAsync(Cancel));
            Assert.False(await db.Relays.AnyAsync(Cancel));
            Assert.False(await db.EventLog.AnyAsync(e => e.BoardId != null, Cancel));
        }

        using var http = boards.App.CreateClient();
        foreach (var page in new[] { "/", "/events", "/settings", "/debug", "/group/new", "/board/1" })
        {
            var response = await http.GetAsync(page, Cancel);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page}: {response.StatusCode}");
        }

        var relays = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        Assert.Contains("There's no relay board in Contactor.", relays, StringComparison.Ordinal);
        var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("There's no relay board in Contactor.", settings, StringComparison.Ordinal);
    }

    // The settings' address and login (install.sh's) are the first board's: the board that becomes first never takes
    // them, which were the removed board's.
    [Fact]
    public async Task Should_KeepTheNextBoardWithoutAnAddress_WhenTheFirstIsRemoved()
    {
        await using var boards = await TwoBoards.StartAsync();

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(1, turnOffFirst: true, John, Cancel));

        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var barn = await db.Boards.SingleAsync(Cancel);
        Assert.Equal((boards.BarnId, "", 9760, "", ""), (barn.Id, barn.Host, barn.Port, barn.Username, barn.Password));
        Assert.False(BoardSetup.SavedEndpoint(barn)!.HasHost);
    }

    // A group of relays 3 → 4 (Fan → Heat, Fan running on for 10 minutes), schedules for relay 2 and the group, and
    // input 1's actions on relay 2 and the group, with a run and an event: everything removing the board deletes.
    private static async Task<int> SeedAsync(TwoBoards boards, int boardId)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var relays = await db.Relays.Where(r => r.BoardId == boardId).ToDictionaryAsync(r => r.Channel, r => r.Id, Cancel);
        var input = await db.Inputs.Where(i => i.BoardId == boardId && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel);
        var group = new RelayGroup { BoardId = boardId, Name = "Heater", SortOrder = 1 };
        group.Members.Add(new RelayGroupMember { RelayId = relays[3], Position = 1, RoleName = "Fan", StartDelaySec = 1, RunOnSec = 600 });
        group.Members.Add(new RelayGroupMember { RelayId = relays[4], Position = 2, RoleName = "Heat", StartDelaySec = 1 });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.Schedules.Add(new Schedule { RelayId = relays[2], Days = ScheduleDays.All, OnTime = new(6, 0), OffTime = new(7, 0), CreatedBy = John });
        db.Schedules.Add(new Schedule { GroupId = group.Id, Days = ScheduleDays.All, OnTime = new(6, 0), OffTime = new(7, 0), CreatedBy = John });
        db.InputActions.Add(new InputAction { InputId = input, RelayId = relays[2] });
        db.InputActions.Add(new InputAction { InputId = input, GroupId = group.Id });
        db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, BoardId = boardId, RelayId = relays[1], Actor = John, Action = EventActions.Rename });
        if (!await db.GroupRuntimes.AnyAsync(r => r.GroupId == group.Id, Cancel))
        {
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id, Phase = GroupPhases.Idle });
        }

        await db.SaveChangesAsync(Cancel);
        return group.Id;
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
