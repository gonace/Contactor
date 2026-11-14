using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>An input's actions: the relays and groups it turns on and off when it closes and opens.</summary>
public sealed class InputActionsTests
{
    private const string John = "john@example.com";

    // A seeded relay's auto-off.
    private const int Relay1MaxOnMinutes = 120;

    private static readonly DateTime Now = new(2026, 11, 12, 13, 30, 0, DateTimeKind.Utc);

    private static readonly string Door = EventActors.ForInput(1);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static InputActionTarget Relay(int id, string name = "") => new(id, id, null, name);

    private static InputActionTarget Group(int id, string name = "") => new(null, null, id, name);

    [Fact]
    public async Task Should_SwitchItsRelays_AsAPersonWould()
    {
        await using var rig = await RigAsync();
        await rig.Board.TurnOnAsync(ChannelMask.Of(2), Cancel);
        await rig.SaveAsync(new InputActionRow(Relay(1), WhenClosed: true, WhenOpened: false), new InputActionRow(Relay(2), false, true));

        await rig.RunAsync(closed: true);

        Assert.True(rig.Board.State.Relays.Contains(1));
        Assert.False(rig.Board.State.Relays.Contains(2));
        Assert.Equal([(1, EventActions.On), (2, EventActions.Off)], (await rig.EventsAsync()).Select(e => (e.RelayId ?? 0, e.Action)));
        Assert.All(await rig.EventsAsync(), e => Assert.Equal(Door, e.Actor));

        // Like a press of its button: the relay gets its auto-off, and counts as switched by a person, not a schedule.
        var runtime = await rig.RuntimeAsync(1);
        Assert.Equal(Door, runtime.TurnedOnBy);
        Assert.Equal(Now.AddMinutes(Relay1MaxOnMinutes), runtime.AutoOffAtUtc);
    }

    [Fact]
    public async Task Should_DoNothing_WhereSetToNothing()
    {
        await using var rig = await RigAsync();
        await rig.SaveAsync(new InputActionRow(Relay(1), null, null), new InputActionRow(Relay(2), true, null));

        await rig.RunAsync(closed: false);
        await rig.RunAsync(closed: true);

        Assert.Equal(ChannelMask.Of(2), rig.Board.State.Relays);
        Assert.Equal([2], (await rig.EventsAsync()).Select(e => e.RelayId ?? 0));
    }

    // A relay its schedule turned on stays the schedule's: no auto-off of the input's, and nothing logged.
    [Fact]
    public async Task Should_LeaveARelayAlone_ThatIsAlreadySo()
    {
        await using var rig = await RigAsync();
        await rig.Board.TurnOnAsync(ChannelMask.Of(1), Cancel);
        await rig.SetTurnedOnByAsync(1, EventActors.Schedule);
        await rig.SaveAsync(new InputActionRow(Relay(1), true, null), new InputActionRow(Relay(2), null, false));

        await rig.RunAsync(closed: true);
        await rig.RunAsync(closed: false);

        Assert.Empty(await rig.EventsAsync());
        var runtime = await rig.RuntimeAsync(1);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));
    }

    // A contact that closes and opens again before the board has reported the first command: the second one still goes.
    [Fact]
    public async Task Should_SendEachCommand_BeforeTheBoardHasReportedTheLast()
    {
        await using var rig = await RigAsync();
        await rig.SaveAsync(new InputActionRow(Relay(1), true, false));
        rig.Board.HoldStatus = true;

        await rig.RunAsync(closed: true);
        await rig.RunAsync(closed: false);
        rig.Board.ReleaseStatus();

        Assert.Equal(ChannelMask.None, rig.Board.State.Relays);
        Assert.Equal([EventActions.On, EventActions.Off], (await rig.EventsAsync()).Select(e => e.Action));
    }

    // The group's first relay is on its way when the input opens again: the stop overtakes it, so the group never runs
    // on with the input open.
    [Fact]
    public async Task Should_StopAGroup_WhoseStartIsStillOnItsWay()
    {
        await using var rig = await RigAsync();
        var groupId = await rig.CreateGroupAsync(1, 2);
        await rig.SaveAsync(new InputActionRow(Group(groupId), true, false));
        rig.Board.HoldStatus = true;

        await rig.RunAsync(closed: true);
        await rig.RunAsync(closed: false);
        rig.Board.ReleaseStatus();
        await rig.Sequencer.StepAsync(Cancel);

        Assert.Equal(ChannelMask.None, rig.Board.State.Relays);
        Assert.Equal([EventActions.GroupOn, EventActions.GroupOff], (await rig.EventsAsync()).Select(e => e.Action));
    }

    [Fact]
    public async Task Should_NotStartAGroup_ThatIsFullyOn()
    {
        await using var rig = await RigAsync();
        var groupId = await rig.CreateGroupAsync(1, 2);
        await rig.SaveAsync(new InputActionRow(Group(groupId), true, false));
        await rig.Board.TurnOnAsync(ChannelMask.Of([1, 2]), Cancel);

        await rig.RunAsync(closed: true);

        Assert.Empty(await rig.EventsAsync());
    }

    // The Input screen keys its cards and choices by this: two groups with one name must not share a key.
    [Fact]
    public void Should_KeyATarget_ByWhatItIs_NotItsName()
    {
        Assert.Equal("group:1", Group(1, "Heater").Key);
        Assert.NotEqual(Group(1, "Heater").Key, Group(2, "Heater").Key);
        Assert.NotEqual(Group(4, "A").Key, Relay(4, "A").Key);
    }

    [Fact]
    public async Task Should_StartAndStopAGroup_InItsOrder()
    {
        await using var rig = await RigAsync();
        var groupId = await rig.CreateGroupAsync(1, 2);
        await rig.SaveAsync(new InputActionRow(Group(groupId), true, false));

        await rig.RunAsync(closed: true);

        // The first relay at once, the next one after its wait: the sequencer's own steps.
        Assert.Equal(ChannelMask.Of(1), rig.Board.State.Relays);
        var start = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.GroupOn);
        Assert.Equal((Door, groupId), (start.Actor, start.GroupId));

        await rig.RunAsync(closed: false);

        Assert.Equal(ChannelMask.None, rig.Board.State.Relays);
        var stop = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.GroupOff);
        Assert.Equal(Door, stop.Actor);
    }

    [Fact]
    public async Task Should_SendNothing_WithoutTheBoard()
    {
        await using var rig = await RigAsync();
        await rig.SaveAsync(new InputActionRow(Relay(1), true, false));
        rig.Board.SetConnected(false);

        await rig.RunAsync(closed: true);
        rig.Board.SetConnected(true);

        Assert.Empty(await rig.EventsAsync());
        Assert.Equal(ChannelMask.None, rig.Board.State.Relays);
    }

    // A relay that joined a group after the action was saved (only possible behind the screens' back) is skipped, and
    // the rest still run.
    [Fact]
    public async Task Should_SkipARelayInAGroup_AndRunTheRest()
    {
        await using var rig = await RigAsync();
        await rig.SaveAsync(new InputActionRow(Relay(1), true, null), new InputActionRow(Relay(3), true, null));
        await rig.CreateGroupAsync(1, 2, check: false);

        await rig.RunAsync(closed: true);

        Assert.Equal(ChannelMask.Of(3), rig.Board.State.Relays);
    }

    [Fact]
    public async Task Should_ListGroupsFirst_AndOfferOnlyRelaysInNoGroup()
    {
        await using var rig = await RigAsync();
        var groupId = await rig.CreateGroupAsync(1, 2);
        await rig.SaveAsync(new InputActionRow(Relay(4), false, true), new InputActionRow(Group(groupId), true, false));

        var rows = await rig.Handler.ListAsync(1, Cancel);
        var targets = await rig.Handler.TargetsAsync(1, Cancel);

        Assert.Equal(["Group: Heater", "Relay 4: Relay 4"], rows.Select(r => r.Target.Label));
        Assert.Equal((false, true), (rows[1].WhenClosed, rows[1].WhenOpened));
        Assert.Equal(["Group: Heater", "Relay 3: Relay 3", "Relay 4: Relay 4"], targets.Select(t => t.Label));
    }

    [Fact]
    public async Task Should_RefuseToSave_WhatItCantControl()
    {
        await using var rig = await RigAsync();
        await rig.CreateGroupAsync(1, 2);

        Assert.Equal("Relay 1 is in a group now: add the group instead.", await rig.Handler.SaveAsync(1, [new(Relay(1), true, null)], Cancel));
        Assert.Equal("That group no longer exists.", await rig.Handler.SaveAsync(1, [new(Group(99), true, null)], Cancel));
        Assert.Equal("Relay 3: Relay 3 is in the list twice.", await rig.Handler.SaveAsync(1, [new(Relay(3, "Relay 3"), true, null), new(Relay(3, "Relay 3"), null, true)], Cancel));
        Assert.Equal("The input doesn't exist.", await rig.Handler.SaveAsync(2, [], Cancel));
        Assert.Equal("The board has no relay 5.", await rig.Handler.SaveAsync(1, [new(Relay(5), true, null)], Cancel));
        Assert.Empty(await rig.Handler.ListAsync(1, Cancel));
    }

    // The Input screen checks the groups and relays before it saves the name: a refusal saves nothing.
    [Fact]
    public async Task Should_CheckWithoutSaving()
    {
        await using var rig = await RigAsync();
        await rig.CreateGroupAsync(1, 2);

        Assert.Equal("Relay 1 is in a group now: add the group instead.", await rig.Handler.CheckAsync(1, [new(Relay(1), true, null)], Cancel));
        Assert.Null(await rig.Handler.CheckAsync(1, [new(Relay(3), true, null)], Cancel));
        Assert.Empty(await rig.Handler.ListAsync(1, Cancel));
    }

    // A relay left from a board that had more is kept, as relays are, but not shown, saved over or switched.
    [Fact]
    public async Task Should_KeepARelayAboveTheBoardsCount_Unseen()
    {
        await using var rig = await RigAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Relays.Add(new Contactor.Web.Persistence.Relay { Id = 6, BoardId = 1, Channel = 6, Name = "Old" });
            db.InputActions.Add(new InputAction { InputId = 1, RelayId = 6, WhenClosed = true });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Empty(await rig.Handler.ListAsync(1, Cancel));
        await rig.SaveAsync(new InputActionRow(Relay(3), true, null));
        await rig.RunAsync(closed: true);

        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal([3, 6], await check.InputActions.OrderBy(a => a.RelayId).Select(a => a.RelayId!.Value).ToListAsync(Cancel));
        Assert.Equal(ChannelMask.Of(3), rig.Board.State.Relays);
    }

    [Fact]
    public async Task Should_KeepARelayItControls_OutOfAGroup()
    {
        await using var rig = await RigAsync();
        await rig.RenameInputAsync("Door");
        await rig.SaveAsync(new InputActionRow(Relay(1), true, false));

        var candidates = await rig.Groups.CandidatesAsync(Cancel);
        var (_, error) = await rig.Groups.CreateAsync(Draft(1, 2), John, Cancel);

        Assert.Equal("Controlled by Door", candidates[0].Note);
        Assert.False(candidates[0].IsFree);
        Assert.Equal("Relay 1 is controlled by the input Door. Remove it there first.", error);
        Assert.DoesNotContain(await rig.Groups.FreeRelaysAsync(Cancel), r => r.Id == 1);
    }

    [Fact]
    public async Task Should_DropAGroupsActions_WithTheGroup()
    {
        await using var rig = await RigAsync();
        var groupId = await rig.CreateGroupAsync(1, 2);
        await rig.SaveAsync(new InputActionRow(Group(groupId), true, false), new InputActionRow(Relay(3), true, false));

        Assert.Null(await rig.Groups.DeleteAsync(groupId, John, Cancel));

        Assert.Equal(["Relay 3: Relay 3"], (await rig.Handler.ListAsync(1, Cancel)).Select(r => r.Target.Label));
    }

    // Only a change the app saw happen acts: one noticed after a gap (here: at start) is never handed on.
    [Fact]
    public async Task Should_HandOnOnlyChanges_SeenAsTheyHappen()
    {
        // The board reads the input closed at its first answer after start.
        await using var rig = await RigAsync(inputOn: true);
        await rig.LogInputAsync(on: false);
        var recorder = rig.Database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            await BoardTestRig.WaitUntilAsync(() => rig.InputEvents() == 2);
            Assert.False(rig.Feed.Reader.TryRead(out _));

            rig.Board.SetInput(1, on: false);
            await BoardTestRig.WaitUntilAsync(() => rig.InputEvents() == 3);

            Assert.True(rig.Feed.Reader.TryRead(out var change));
            Assert.Equal(new InputChanged(1, Closed: false), change);
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Should_NameTheInput_InEvents()
    {
        var groupOff = EventText.For(new EventLogEntry { Actor = Door, Action = EventActions.GroupOff, GroupId = 1 }, null, "Door", "Fan heater");
        var on = EventText.For(new EventLogEntry { Actor = Door, Action = EventActions.On, RelayId = 4 }, "Lighting", null);

        Assert.Equal(new EventView(false, "I", "Door", "turned off the group", "Fan heater"), groupOff);
        Assert.Equal(new EventView(false, "I", "Input 1", "turned on", "Lighting"), on);
        Assert.Equal("Door", EventText.ShortWho(Door, "Door"));
    }

    [Theory]
    [InlineData("input:1", true, 1)]
    [InlineData("input:12", true, 12)]
    [InlineData("input:0", false, 0)]
    [InlineData("input:-1", false, 0)]
    [InlineData("input:", false, 0)]
    [InlineData("input:1x", false, 0)]
    [InlineData("john@example.com", false, 0)]
    [InlineData("schedule", false, 0)]
    [InlineData(null, false, 0)]
    public void Should_ReadAnInputActor(string? actor, bool isInput, int inputId)
    {
        Assert.Equal(isInput, EventActors.TryInput(actor, out var id));
        Assert.Equal(inputId, isInput ? id : 0);
    }

    [Fact]
    public async Task Should_ShowWhatTheInputControls_OnItsScreen()
    {
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John });
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            db.InputActions.Add(new InputAction { InputId = 1, RelayId = 4, WhenClosed = false, WhenOpened = true });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/input/1", Cancel));

        Assert.Contains("Groups and relays", html, StringComparison.Ordinal);
        Assert.Contains("Relay 4: Relay 4", html, StringComparison.Ordinal);
        Assert.Contains("CLOSES", html, StringComparison.Ordinal);
        Assert.Contains("+ Add a group or relay", html, StringComparison.Ordinal);
    }

    private static GroupDraft Draft(params int[] relays)
    {
        return new GroupDraft("Heater", GroupIcons.All[0], 120, [.. relays.Select(r => new GroupMemberDraft(r, null))]);
    }

    private static async Task<Rig> RigAsync(bool inputOn = false)
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 1);
        if (inputOn)
        {
            board.SetInput(1, on: true);
        }
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddInputs(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 1, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        return new Rig(board, database);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(FakeRelayBoard board, TestDatabase database)
        {
            Board = board;
            Database = database;
        }

        public FakeRelayBoard Board { get; }

        public TestDatabase Database { get; }

        public InputActionsHandler Handler => Database.Services.GetRequiredService<InputActionsHandler>();

        public GroupsHandler Groups => Database.Services.GetRequiredService<GroupsHandler>();

        public GroupSequencer Sequencer => Database.Services.GetRequiredService<GroupSequencer>();

        public InputChangeFeed Feed => Database.Services.GetRequiredService<InputChangeFeed>();

        private InputActionService Service => Database.Services.GetServices<IHostedService>().OfType<InputActionService>().Single();

        public async Task SaveAsync(params InputActionRow[] rows)
        {
            Assert.Null(await Handler.SaveAsync(1, rows, Cancel));
        }

        public async Task RunAsync(bool closed)
        {
            await Service.RunAsync(new InputChanged(1, closed), Cancel);
        }

        // Either through the handler, as "New group" does, or straight into the database (check: false), as for a
        // relay that joined a group after an input's action was saved for it.
        public async Task<int> CreateGroupAsync(int first, int second, bool check = true)
        {
            if (check)
            {
                var (id, error) = await Groups.CreateAsync(Draft(first, second), John, Cancel);
                Assert.Null(error);
                return id!.Value;
            }

            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            var group = new RelayGroup { BoardId = 1, Name = "Heater", Icon = GroupIcons.All[0] };
            group.Members.Add(new RelayGroupMember { RelayId = first, Position = 1, RoleName = "A" });
            group.Members.Add(new RelayGroupMember { RelayId = second, Position = 2, RoleName = "B" });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            return group.Id;
        }

        public async Task<List<EventLogEntry>> EventsAsync()
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            return await db.EventLog.AsNoTracking()
                .Where(e => e.Action != EventActions.GroupCreate && e.Action != EventActions.GroupDelete)
                .OrderBy(e => e.Id)
                .ToListAsync(Cancel);
        }

        public int InputEvents()
        {
            using var db = Database.Factory.CreateDbContext();
            return db.EventLog.Count(e => e.Action == EventActions.InputChanged);
        }

        public async Task<RelayRuntime> RuntimeAsync(int relayId)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
        }

        public async Task SetTurnedOnByAsync(int relayId, string actor)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, Cancel);
            (runtime.OnSinceUtc, runtime.TurnedOnBy, runtime.AutoOffAtUtc) = (Now, actor, null);
            await db.SaveChangesAsync(Cancel);
        }

        public async Task RenameInputAsync(string name)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            (await db.Inputs.SingleAsync(i => i.Id == 1, Cancel)).Name = name;
            await db.SaveChangesAsync(Cancel);
        }

        public async Task LogInputAsync(bool on)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = Now.AddHours(-1),
                InputId = 1,
                Actor = EventActors.External,
                Action = EventActions.InputChanged,
                Details = on ? """{"on":true}""" : """{"on":false}""",
            });
            await db.SaveChangesAsync(Cancel);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
        }
    }
}
