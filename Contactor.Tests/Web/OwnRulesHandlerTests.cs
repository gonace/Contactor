using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// A person turning off or keeping a board's own rules (<see cref="OwnRulesHandler"/>): sent at once to that board only
/// and logged, or nothing at all without a connection, never queued.
/// </summary>
public sealed class OwnRulesHandlerTests
{
    private const string Erik = OwnRulesRig.Erik;

    private static readonly OwnRule Timer3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule Timer5 = new(5, OwnRuleKind.Timer);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_TurnOffOnlyThatBoardsRules_AndLogThePersonsEvent()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        rig.Other.SetOwnRules(new OwnRules([Timer3]));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2 && rig.Service.Rows(rig.OtherId).Count == 1);

        Assert.Null(await rig.Handler.TurnOffAsync(rig.BoardId, [Timer5, Timer3], Erik, Cancel));

        Assert.Equal(OwnRules.Empty, rig.Board.State.OwnRules);
        Assert.Equal(new OwnRules([Timer3]), rig.Other.State.OwnRules);
        var entry = Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRulesTurnedOff));
        Assert.Equal((Erik, (int?)null, """{"relays":[3,5],"kinds":["timer"]}"""), (entry.Actor, entry.RelayId, entry.Details));
        Assert.Empty(await rig.EventsAsync(EventActions.BoardOwnRulesTurnedOff, rig.OtherId));

        // The board's next state has none: their rows go, without another event.
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 0);
        Assert.Empty(await rig.RowsAsync());
        Assert.Single(await rig.RowsAsync(rig.OtherId));
        Assert.Equal(2, (await rig.EventsAsync(EventActions.BoardOwnRuleFound)).Count);
    }

    // Relay 3's timer takes relay 5's auto-on with it: the handler says so for that board only, and a turn-off of relay
    // 3's logs relay 3's with the auto-on as "also", while the auto-on's row goes with the board's next state as well.
    [Fact]
    public async Task Should_KnowWhatGoesWithATurnOff_AndLogItAsAlso()
    {
        var autoOn5 = new OwnRule(5, OwnRuleKind.AutoOn);
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3, autoOn5], [[Timer3, autoOn5], [Timer3]]));
        rig.Other.SetOwnRules(new OwnRules([Timer3, autoOn5]));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2 && rig.Service.Rows(rig.OtherId).Count == 2);

        Assert.Equal([autoOn5], rig.Handler.AlsoTurnsOff(rig.BoardId, [Timer3]));
        Assert.Empty(rig.Handler.AlsoTurnsOff(rig.BoardId, [autoOn5]));
        Assert.Empty(rig.Handler.AlsoTurnsOff(rig.BoardId, [Timer3, autoOn5]));
        Assert.Empty(rig.Handler.AlsoTurnsOff(rig.OtherId, [Timer3]));
        Assert.Empty(rig.Handler.AlsoTurnsOff(99, [Timer3]));

        Assert.Null(await rig.Handler.TurnOffAsync(rig.BoardId, [Timer3], Erik, Cancel));

        var entry = Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRulesTurnedOff));
        Assert.Equal("""{"relays":[3],"kinds":["timer"],"also":[{"relay":5,"kind":"auto-on"}]}""", entry.Details);
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 0);
        Assert.Equal(2, rig.Service.Rows(rig.OtherId).Count);
        Assert.Contains(await rig.EventsAsync(EventActions.BoardOwnRuleFound), e => e.Details!.Contains("\"kind\":\"auto-on\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SendNothingAndLogNothing_WhenNoneAreAsked()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3]));

        Assert.Null(await rig.Handler.TurnOffAsync(rig.BoardId, [], Erik, Cancel));

        Assert.Equal(new OwnRules([Timer3]), rig.Board.State.OwnRules);
        Assert.Empty(await rig.EventsAsync(EventActions.BoardOwnRulesTurnedOff));
    }

    [Fact]
    public async Task Should_SayWhy_WhenTheBoardDoesntTakeThem()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3]));

        // The fake board takes none it doesn't have, as a real board's client throws when it took none.
        Assert.Equal("The relay board didn't turn them off. Try again.", await rig.Handler.TurnOffAsync(rig.BoardId, [Timer5], Erik, Cancel));
        Assert.Equal("The relay board didn't turn them off. Try again.", await rig.Handler.TurnOffAsync(rig.BoardId, [new OwnRule(9, OwnRuleKind.Timer)], Erik, Cancel));

        rig.Board.SetConnected(false);
        Assert.Equal("No contact with the relay board. Nothing was turned off.", await rig.Handler.TurnOffAsync(rig.BoardId, [Timer3], Erik, Cancel));

        Assert.Equal(new OwnRules([Timer3]), rig.Board.State.OwnRules);
        Assert.Empty(await rig.EventsAsync(EventActions.BoardOwnRulesTurnedOff));
    }

    // Offline: the press says so, nothing is sent or logged, and nothing is sent later either: after the reconnect the
    // board's timers are still on.
    [Fact]
    public async Task Should_SendNothing_Offline_AndNothingAfterTheReconnect()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x14);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var database = OwnRulesServiceTests.Vm201Database(simulator, time);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var board = await database.Services.UseBoardAsync("vm201");
        var client = database.Services.BoardService<Vm201Client>();
        var handler = database.Services.GetRequiredService<OwnRulesHandler>();
        var services = OwnRulesServiceTests.Started(database);
        foreach (var service in services)
        {
            await service.StartAsync(Cancel);
        }

        try
        {
            await Wait.UntilAsync(() => OwnRulesServiceTests.Found(database) == 2);
            simulator.DropConnections();
            await Wait.UntilAsync(() => !client.State.IsConnected);

            var message = await handler.TurnOffAsync(board.BoardId, [Timer3, Timer5], Erik, Cancel);

            await Wait.UntilAsync(() =>
            {
                if (simulator.ConnectionCount >= 2 && client.State.IsConnected)
                {
                    return true;
                }

                time.Advance(TimeSpan.FromSeconds(1));
                return false;
            });
            await OwnRulesServiceTests.KeepAliveAsync(simulator, time);
            Assert.Equal("No contact with the relay board. Nothing was turned off.", message);
        }
        finally
        {
            foreach (var service in services)
            {
                await service.StopAsync(Cancel);
            }
        }

        Assert.Equal(0x14, simulator.TimerMask);
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.EventLog.AnyAsync(e => e.Action == EventActions.BoardOwnRulesTurnedOff, Cancel));
    }

    // Online, the VM201 gets one CMD_TMR_DIS with only the bits asked for, and the rule left stays remembered.
    [Fact]
    public async Task Should_TurnOffTheVm201sTimersAsked_AndForgetTheirRows()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x14);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var database = OwnRulesServiceTests.Vm201Database(simulator, time);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var board = await database.Services.UseBoardAsync("vm201");
        var handler = database.Services.GetRequiredService<OwnRulesHandler>();
        var rules = database.Services.GetRequiredService<OwnRulesService>();
        var services = OwnRulesServiceTests.Started(database);
        foreach (var service in services)
        {
            await service.StartAsync(Cancel);
        }

        try
        {
            await Wait.UntilAsync(() => rules.Rows(board.BoardId).Count == 2);

            Assert.Null(await handler.TurnOffAsync(board.BoardId, [Timer3], Erik, Cancel));

            await Wait.UntilAsync(() => simulator.TimerMask == 0x10 && rules.Rows(board.BoardId).Count == 1);
        }
        finally
        {
            foreach (var service in services)
            {
                await service.StopAsync(Cancel);
            }
        }

        var sent = Assert.Single(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        Assert.Equal([0x04], sent.Data.ToArray());
        Assert.Equal((5, OwnRuleKind.Timer), (Assert.Single(rules.Rows(board.BoardId)).Channel, Assert.Single(rules.Rows(board.BoardId)).Kind));
    }

    // A new address deletes the row while a "Keep" is under way: the person is told, and nothing is saved or logged.
    [Fact]
    public async Task Should_SayTheRuleIsGone_WhenItsRowGoesWhileKept()
    {
        using var held = new HeldQueries();
        await using var rig = await OwnRulesRig.StartAsync(held.AddTo);
        rig.Board.SetOwnRules(new OwnRules([Timer3]));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);

        // Held after it read the row, at the relay's id.
        held.Hold(sql => sql.Contains("FROM \"Relay\" AS", StringComparison.Ordinal));
        var keeping = rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel);
        await Wait.UntilAsync(() => held.Holds == 1);
        var address = rig.Database.Services.GetRequiredService<Contactor.Web.Features.Boards.BoardAddressHandler>();
        Assert.Null(await address.SaveAsync(rig.BoardId, "10.0.0.9", 9760, Erik, Cancel));
        held.Release();

        Assert.Equal("The board no longer has that rule.", await keeping);
        Assert.Empty(await rig.RowsAsync());
        Assert.Empty(await rig.EventsAsync(EventActions.BoardOwnRuleKept));
    }

    // Just after the start the service hasn't read which rules people kept: no warning until it has, so a kept rule
    // never shows as one.
    [Fact]
    public async Task Should_WarnAboutNothing_UntilTheRowsAreRead()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2);
        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));

        using var fresh = new OwnRulesService(
            rig.Database.Services.GetRequiredService<Contactor.Web.Features.Boards.BoardRegistry>(),
            rig.Database.Factory,
            rig.Database.Services.GetRequiredService<Contactor.Web.LocalClock>(),
            rig.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnRulesService>.Instance);
        var handler = ActivatorUtilities.CreateInstance<OwnRulesHandler>(rig.Database.Services, fresh);

        Assert.Empty(handler.Warnings(rig.BoardId));

        await fresh.StartAsync(Cancel);
        try
        {
            await Wait.UntilAsync(() => fresh.TryGetRows(rig.BoardId, out _));
            Assert.Equal([Timer5], handler.Warnings(rig.BoardId));
        }
        finally
        {
            await fresh.StopAsync(Cancel);
        }
    }

    [Fact]
    public async Task Should_KeepARule_AndLogItOnce()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2);

        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));
        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));

        var rows = await rig.RowsAsync();
        Assert.Equal(Erik, rows[0].KeptBy);
        Assert.NotNull(rows[0].KeptUtc);
        Assert.Null(rows[1].KeptUtc);
        Assert.Equal(Erik, rig.Service.Rows(rig.BoardId)[0].KeptBy);
        var entry = Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRuleKept));
        Assert.Equal((Erik, await rig.RelayIdAsync(3), """{"kind":"timer","relay":3}"""), (entry.Actor, entry.RelayId, entry.Details));
        Assert.Equal([Timer5], rig.Handler.Warnings(rig.BoardId));

        // A rule the app doesn't have.
        Assert.Equal("The board no longer has that rule.", await rig.Handler.KeepAsync(rig.BoardId, 4, OwnRuleKind.Timer, Erik, Cancel));
        Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRuleKept));
    }

    // "Keep them" while a board is added never waits for the service: here its save of the rules fails (tried again 30
    // s later), and keeping them makes their rows, logged as found once each, and kept. When the service tries again it
    // finds them and logs nothing more. A rule the board no longer reports keeps nothing.
    [Fact]
    public async Task Should_KeepAll_AlsoRulesTheServiceHasntSavedYet_AndLogThemFoundOnce()
    {
        var failing = new FailingQueries();
        await using var rig = await OwnRulesRig.StartAsync(failing.AddTo);
        var inserts = 0;
        failing.Fail(
            sql => sql.Contains("INSERT INTO \"BoardOwnRule\"", StringComparison.Ordinal) && Interlocked.Increment(ref inserts) == 1,
            () => new InvalidOperationException("The disk is full."));
        rig.Board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        await Wait.UntilAsync(() => failing.Failures == 1);
        Assert.Empty(await rig.RowsAsync());

        Assert.Equal("The board no longer has that rule.", await rig.Handler.KeepAllAsync(rig.BoardId, [Timer3, new OwnRule(4, OwnRuleKind.Timer)], Erik, Cancel));
        Assert.Empty(await rig.RowsAsync());
        Assert.Empty(await rig.EventsAsync(EventActions.BoardOwnRuleKept));

        Assert.Null(await rig.Handler.KeepAllAsync(rig.BoardId, [Timer3, Timer5, Timer3], Erik, Cancel));

        var rows = await rig.RowsAsync();
        Assert.Equal([3, 5], rows.Select(r => r.Channel));
        Assert.All(rows, r => Assert.Equal((true, Erik), (r.KeptUtc is not null, r.KeptBy)));
        Assert.Equal(2, rig.Service.Rows(rig.BoardId).Count(r => r.KeptBy == Erik));
        Assert.Equal(2, (await rig.EventsAsync(EventActions.BoardOwnRuleFound)).Count);
        var kept = await rig.EventsAsync(EventActions.BoardOwnRuleKept);
        Assert.Equal((Erik, await rig.RelayIdAsync(3)), (kept[0].Actor, kept[0].RelayId));
        Assert.Equal(2, kept.Count);
        Assert.Empty(rig.Handler.Warnings(rig.BoardId));

        // Kept already: nothing more is logged.
        Assert.Null(await rig.Handler.KeepAllAsync(rig.BoardId, [Timer3], Erik, Cancel));
        Assert.Equal(2, (await rig.EventsAsync(EventActions.BoardOwnRuleKept)).Count);

        rig.Time.Advance(OwnRulesService.RetryDelay);
        await rig.HandledAsync();
        await rig.HandledAsync();
        Assert.Equal(2, (await rig.EventsAsync(EventActions.BoardOwnRuleFound)).Count);
        Assert.Equal(2, (await rig.RowsAsync()).Count(r => r.KeptBy == Erik));
    }
}
