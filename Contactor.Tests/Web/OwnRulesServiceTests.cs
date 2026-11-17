using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.OwnRules;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>
/// What the app remembers and logs about a board's own rules (<see cref="OwnRulesService"/>): found once, gone without
/// a word, null changing nothing, a new address or model forgetting them, and never a command to the board.
/// </summary>
public sealed class OwnRulesServiceTests
{
    private const string Erik = OwnRulesRig.Erik;

    private static readonly OwnRule Timer3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule Timer5 = new(5, OwnRuleKind.Timer);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogARuleOnce_AsFoundAtFirstSight_ThenAsNewWhenTurnedOnWhileWatched()
    {
        await using var rig = await OwnRulesRig.StartAsync();

        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);

        // The same rules again, also with a relay's change, log nothing more.
        rig.Board.SetOwnRules(Rules(Timer3));
        rig.Board.SetRelay(1, on: true);
        rig.Board.SetOwnRules(Rules(Timer3, Timer5));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2);

        var found = await rig.EventsAsync(EventActions.BoardOwnRuleFound);
        Assert.Equal(2, found.Count);
        Assert.All(found, e => Assert.Equal(EventActors.System, e.Actor));
        Assert.Equal((await rig.RelayIdAsync(3), """{"kind":"timer","new":false,"relay":3}"""), (found[0].RelayId, found[0].Details));
        Assert.Equal((await rig.RelayIdAsync(5), """{"kind":"timer","new":true,"relay":5}"""), (found[1].RelayId, found[1].Details));
        Assert.Equal([(3, "timer"), (5, "timer")], (await rig.RowsAsync()).Select(r => (r.Channel, r.Kind)));
        Assert.All(await rig.RowsAsync(), r => Assert.Null(r.KeptUtc));
        Assert.Equal([Timer3, Timer5], rig.Handler.Warnings(rig.BoardId));
    }

    // A restart: the service starts again with the row there, and the board reports the same rule. Nothing is logged.
    [Fact]
    public async Task Should_LogNothing_AfterARestart_WhenTheRuleHasItsRow()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);
        await rig.Service.StopAsync(Cancel);

        using var again = new OwnRulesService(
            rig.Database.Services.GetRequiredService<BoardRegistry>(),
            rig.Database.Factory,
            rig.Database.Services.GetRequiredService<LocalClock>(),
            TimeProvider.System,
            NullLogger<OwnRulesService>.Instance);
        await again.StartAsync(Cancel);
        try
        {
            await Wait.UntilAsync(() => again.Rows(rig.BoardId).Count == 1);
            rig.Board.SetOwnRules(Rules(Timer3, Timer5));
            await Wait.UntilAsync(() => again.Rows(rig.BoardId).Count == 2);
        }
        finally
        {
            await again.StopAsync(Cancel);
        }

        Assert.Equal([3, 5], (await rig.EventsAsync(EventActions.BoardOwnRuleFound)).Select(e => e.RelayId).Select(RelayChannel(rig)));
    }

    [Fact]
    public async Task Should_DeleteTheRowAndItsKeep_WhenTheRuleIsGone_AndWarnAgain_WhenItComesBack()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);
        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));
        Assert.Empty(rig.Handler.Warnings(rig.BoardId));

        rig.Board.SetOwnRules(OwnRules.Empty);
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 0);
        Assert.Empty(await rig.RowsAsync());
        Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRuleFound));

        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);

        var row = Assert.Single(await rig.RowsAsync());
        Assert.Equal((null, null), (row.KeptUtc, row.KeptBy));
        var found = await rig.EventsAsync(EventActions.BoardOwnRuleFound);
        Assert.Equal([false, true], found.Select(OwnRulesRig.IsNew));
        Assert.Equal([Timer3], rig.Handler.Warnings(rig.BoardId));
    }

    // No reading: the board can't read its rules now, or hasn't answered. The rows and their "Keep" stay.
    [Fact]
    public async Task Should_KeepTheRows_WhenTheBoardReportsNoRules()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);
        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));

        rig.Board.SetOwnRules(null);
        rig.Board.SetConnected(false);
        await rig.HandledAsync();

        var row = Assert.Single(await rig.RowsAsync());
        Assert.Equal(Erik, row.KeptBy);
        Assert.Equal(Erik, Assert.Single(rig.Service.Rows(rig.BoardId)).KeptBy);
        Assert.Empty(rig.Handler.Warnings(rig.BoardId));

        // Back with the same rule: still kept, and not found again.
        rig.Board.SetConnected(true);
        rig.Board.SetOwnRules(Rules(Timer3));
        await rig.HandledAsync();
        Assert.Equal(Erik, Assert.Single(await rig.RowsAsync()).KeptBy);
        Assert.Single(await rig.EventsAsync(EventActions.BoardOwnRuleFound));
    }

    // Another board may be at a new address: its rows go, with their "Keep", and its rules are found afresh.
    [Fact]
    public async Task Should_ForgetTheRows_AtANewAddress()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(Rules(Timer3));
        rig.Other.SetOwnRules(Rules(new OwnRule(2, OwnRuleKind.Timer)));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1 && rig.Service.Rows(rig.OtherId).Count == 1);
        Assert.Null(await rig.Handler.KeepAsync(rig.BoardId, 3, OwnRuleKind.Timer, Erik, Cancel));

        var address = rig.Database.Services.GetRequiredService<BoardAddressHandler>();
        Assert.Null(await address.SaveAsync(rig.BoardId, "10.0.0.9", 9760, Erik, Cancel));

        Assert.Empty(await rig.RowsAsync());
        Assert.Single(await rig.RowsAsync(rig.OtherId));

        // The client forgets the old board's state at once, then the board there answers with the same rule.
        rig.Board.Forget();
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 0);
        rig.Board.SetConnected(true);
        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);

        Assert.Null(Assert.Single(await rig.RowsAsync()).KeptUtc);
        Assert.Equal([false, false], (await rig.EventsAsync(EventActions.BoardOwnRuleFound)).Select(OwnRulesRig.IsNew));
        Assert.Equal([Timer3], rig.Handler.Warnings(rig.BoardId));
    }

    // The same address saved again is no new board: the rows stay.
    [Fact]
    public async Task Should_KeepTheRows_WhenTheSameAddressIsSaved()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        var address = rig.Database.Services.GetRequiredService<BoardAddressHandler>();
        Assert.Null(await address.SaveAsync(rig.BoardId, "10.0.0.9", 9760, Erik, Cancel));
        rig.Board.SetOwnRules(Rules(Timer3));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 1);

        Assert.Null(await address.SaveAsync(rig.BoardId, "10.0.0.9", 9760, Erik, Cancel));
        await rig.HandledAsync();

        Assert.Single(await rig.RowsAsync());
        Assert.Single(rig.Service.Rows(rig.BoardId));
    }

    [Fact]
    public async Task Should_ForgetTheRows_WithAnotherModel()
    {
        await using var rig = await OwnRulesRig.StartAsync();
        rig.Board.SetOwnRules(Rules(Timer3, Timer5));
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 2);

        var registry = rig.Database.Services.GetRequiredService<BoardRegistry>();
        await registry.ReplaceModelAsync(rig.BoardId, BoardCatalogue.Find("vm204")!.Model, Cancel);

        Assert.Empty(await rig.RowsAsync());
        await Wait.UntilAsync(() => rig.Service.Rows(rig.BoardId).Count == 0);
        Assert.Empty(rig.Handler.Warnings(rig.BoardId));
    }

    // The principle: the app never changes a board's own rules without a person's press. The VM201's timers stay as
    // they are while the app runs for 10 minutes on its clock, keep-alives and all, and across a reconnect.
    // Vm201TimersService still turns the timers off at the first connection until part 6 of issue #497 (with
    // Vm201:DisableBoardTimers), so it is turned off here: what is tested is everything else.
    [Fact]
    public async Task Should_NeverChangeTheBoardsTimers_WithoutAPress()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x14);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var database = Vm201Database(simulator, time);
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.UseBoardAsync("vm201");
        var client = database.Services.BoardService<Vm201Client>();
        var services = Started(database);
        foreach (var service in services)
        {
            await service.StartAsync(Cancel);
        }

        try
        {
            await Wait.UntilAsync(() => Found(database) == 2);

            for (var i = 0; i < 40; i++)
            {
                var asked = StatusRequests(simulator);
                time.Advance(TimeSpan.FromSeconds(15));
                await Wait.UntilAsync(() => StatusRequests(simulator) > asked);
            }

            simulator.DropConnections();
            await Wait.UntilAsync(() => !client.State.IsConnected || simulator.ConnectionCount >= 2);
            await Wait.UntilAsync(() =>
            {
                if (simulator.ConnectionCount >= 2 && client.State.IsConnected)
                {
                    return true;
                }

                // The client waits for its next attempt on the app's clock.
                time.Advance(TimeSpan.FromSeconds(1));
                return false;
            });

            var before = StatusRequests(simulator);
            time.Advance(TimeSpan.FromSeconds(15));
            await Wait.UntilAsync(() => StatusRequests(simulator) > before && client.State.TimerMask == 0x14);
        }
        finally
        {
            foreach (var service in services)
            {
                await service.StopAsync(Cancel);
            }
        }

        Assert.Equal(0x14, simulator.TimerMask);
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command is Vm201Command.TimerDisable or Vm201Command.TimerEnable or Vm201Command.TimerToggle);
        Assert.Equal(2, Found(database));
    }

    internal static TestDatabase Vm201Database(Vm201Simulator simulator, TimeProvider time)
    {
        return TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddSingleton(time);
                services.AddLocalClock(configuration);
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
                services.AddOwnRules(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),

                // Until part 6 of issue #497 the VM201's timers are turned off at its first connection unless this says not to.
                ["Vm201:DisableBoardTimers"] = "false",
            });
    }

    // The own rules first, then the boards' connections, as in the app.
    internal static List<IHostedService> Started(TestDatabase database)
    {
        var hosted = database.Services.GetServices<IHostedService>().ToList();
        return [.. hosted.OfType<OwnRulesService>(), .. hosted.OfType<BoardHostedService>()];
    }

    internal static int Found(TestDatabase database)
    {
        using var db = database.Factory.CreateDbContext();
        return db.EventLog.Count(e => e.Action == EventActions.BoardOwnRuleFound);
    }

    private static int StatusRequests(Vm201Simulator simulator)
    {
        return simulator.ReceivedCommands.Count(p => p.Command == Vm201Command.StatusRequest);
    }

    private static Func<int?, int> RelayChannel(OwnRulesRig rig)
    {
        using var db = rig.Database.Factory.CreateDbContext();
        var channels = db.Relays.Where(r => r.BoardId == rig.BoardId).ToDictionary(r => r.Id, r => r.Channel);
        return id => id is { } relayId ? channels[relayId] : 0;
    }

    private static OwnRules Rules(params OwnRule[] rules)
    {
        return new OwnRules(rules);
    }
}
