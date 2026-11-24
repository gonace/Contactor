using System.Collections.Concurrent;
using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>Every board the app runs, one live board per board's row, and replacing a board's model while the app runs.</summary>
public sealed class BoardRegistryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Boards saved before this version seeded analog inputs: each gets the rows its model lacks when it is loaded, the
    // first board and the others alike, and rows already there keep their names and scales.
    [Fact]
    public async Task Should_SeedTheRowsABoardLacks_WhenItIsLoaded_AndKeepThoseItHas()
    {
        await using var database = await DatabaseAsync();
        var first = await AddRowAsync(database, type: "vm201", sortOrder: 1, name: "Workshop");
        var garage = await AddRowAsync(database, type: "vm204", sortOrder: 2, name: "Garage");
        var barn = await AddRowAsync(database, type: "vm204", sortOrder: 3, name: "Barn");
        var vm204 = Vm204Model.Model.Capabilities;
        await database.Services.SeedBoardAsync(first, 8, 1, Cancel);
        await database.Services.SeedBoardAsync(garage, vm204.RelayCount, vm204.InputCount, Cancel);
        await database.Services.SeedBoardAsync(barn, vm204, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var input = await db.AnalogInputs.SingleAsync(i => i.BoardId == barn && i.Channel == 1, Cancel);
            (input.Name, input.Unit, input.Volts1, input.Value1, input.Volts2, input.Value2) = ("Paint shop", AnalogUnits.Celsius, 0m, -20m, 5m, 80m);
            await db.SaveChangesAsync(Cancel);
            Assert.False(await db.AnalogInputs.AnyAsync(i => i.BoardId == garage, Cancel));
        }

        await Registry(database).LoadAsync(Cancel);

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var rows = await db.AnalogInputs.AsNoTracking().OrderBy(i => i.BoardId).ThenBy(i => i.Channel).ToListAsync(Cancel);
            Assert.Equal(
                [.. Enumerable.Range(1, vm204.AnalogInputCount).Select(c => (garage, c, $"Analog {c}", false)),
                 .. Enumerable.Range(1, vm204.AnalogInputCount).Select(c => (barn, c, c == 1 ? "Paint shop" : $"Analog {c}", c == 1))],
                rows.Select(i => (i.BoardId, i.Channel, i.Name, i.HasScale)));
            Assert.Equal((AnalogUnits.Celsius, 80m), (rows.Single(i => i.BoardId == barn && i.Channel == 1).Unit, rows.Single(i => i.BoardId == barn && i.Channel == 1).Value2));
            Assert.Equal(8, await db.Relays.CountAsync(r => r.BoardId == first, Cancel));
        }
    }

    [Fact]
    public async Task Should_HaveNoRelaysAndRefuseCommands_BeforeItsModelIsChosen()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database, type: "");
        var registry = Registry(database);

        await registry.LoadAsync(Cancel);

        var board = Assert.Single(registry.All);
        Assert.Same(board, registry.Find(id));
        Assert.Null(registry.Find(id + 1));
        Assert.Null(board.Active);
        Assert.Same(LiveBoard.NoBoard, board.Capabilities);
        Assert.Same(BoardState.Initial, board.State);
        Assert.False(board.Endpoint.HasHost);
        Assert.True(board.Login.IsNone);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOffAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));
        Assert.Throws<InvalidOperationException>(() => board.SetEndpoint(new BoardEndpoint("127.0.0.1", 9760)));
        Assert.Throws<InvalidOperationException>(() => board.SetLogin(BoardLogin.None));
        Assert.Equal("No relay board has been chosen yet.", Assert.Throws<InvalidOperationException>(() => board.CheckLogin(BoardLogin.None)).Message);
        Assert.False(board.TryCheckLogin(new BoardLogin("admin", "secret"), out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public async Task Should_PassEverythingToTheBoardsModel()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var parts = new FakeBoardParts(relayCount: 4);

        var board = await Registry(database).ReplaceAsync(parts.Build(id, Vm204Model.Model), Cancel);
        await board.TurnOnAsync(ChannelMask.Of(2), Cancel);
        board.SetEndpoint(new BoardEndpoint("10.0.0.9", 80));

        Assert.Equal(Vm204Model.Model, board.Model);
        Assert.Same(parts.Board.Capabilities, board.Capabilities);
        Assert.True(board.State.IsOn(2));
        Assert.Equal(new BoardEndpoint("10.0.0.9", 80), parts.Connection.Endpoint);
        Assert.Equal(parts.Connection.Endpoint, board.Endpoint);
    }

    // The new model is unknown until it answers, as at a new address, so the screens and the recorder start afresh;
    // nothing more comes from the old one, to the board's listeners or the registry's.
    [Fact]
    public async Task Should_ReportTheNewModelsState_AtTheReplacement_AndNothingMoreFromTheOldOne()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();
        var next = new FakeBoardParts(connected: false);
        var board = await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        var states = new List<BoardState>();
        var changes = new List<BoardStateChange>();
        var traffic = new List<BoardTraceEntry>();
        board.StateChanged += (_, state) => states.Add(state);
        registry.StateChanged += (_, change) => changes.Add(change);
        board.Traced += (_, entry) => traffic.Add(entry);

        await registry.ReplaceAsync(next.Build(id, Vm204Model.Model), Cancel);
        old.Fake.SetRelay(1, on: true);
        old.Traffic.Trace("old");
        next.Fake.SetRelay(2, on: true);
        next.Traffic.Trace("new");

        Assert.Equal(2, states.Count);
        Assert.False(states[0].HasAnswered);
        Assert.True(states[1].IsOn(2));
        Assert.Equal(states.Select(s => new BoardStateChange(id, s)), changes);
        Assert.Equal("new", Assert.Single(traffic).Text);
        Assert.Equal("new", Assert.Single(board.Trace.Items).Text);
        Assert.False(old.Fake.HasListeners);
        Assert.False(old.Traffic.HasListeners);
        Assert.True(old.Connection.Disposed);
    }

    // An analog value alone is no state: the board's state listeners (the safety services) never hear of it.
    [Fact]
    public async Task Should_PassOnAnalogChanges_OfTheCurrentModelOnly_AndNotAsStates()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();
        var next = new FakeBoardParts();
        var board = await registry.ReplaceAsync(old.Build(id, Vm204Model.Model), Cancel);
        var states = new List<BoardState>();
        var analogs = new List<BoardState>();
        board.StateChanged += (_, state) => states.Add(state);
        board.AnalogChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.AnalogChanged += (_, state) => analogs.Add(state);

        old.Fake.SetAnalog(new AnalogReadings([512]));
        Assert.Empty(states);
        Assert.Equal(new AnalogReadings([512]), Assert.Single(analogs).Analog);

        await registry.ReplaceAsync(next.Build(id, Vm204Model.Model), Cancel);
        old.Fake.SetAnalog(new AnalogReadings([600]));
        next.Fake.SetAnalog(new AnalogReadings([700]));

        Assert.Equal(2, analogs.Count);
        Assert.Equal(new AnalogReadings([700]), analogs[1].Analog);
        Assert.False(old.Fake.HasListeners);
    }

    // The board's own rules alone are no state either; they reach the registry with the board's id.
    [Fact]
    public async Task Should_PassOnOwnRulesChanges_OfTheCurrentModelOnly_AndNotAsStates()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts(new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff));
        var next = new FakeBoardParts(new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff));
        var board = await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        var states = new List<BoardState>();
        var rules = new List<BoardState>();
        var changes = new List<BoardStateChange>();
        board.StateChanged += (_, state) => states.Add(state);
        board.OwnRulesChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.OwnRulesChanged += (_, state) => rules.Add(state);
        registry.OwnRulesChanged += (_, change) => changes.Add(change);
        var timer = new OwnRules([new OwnRule(3, OwnRuleKind.Timer)]);

        old.Fake.SetOwnRules(timer);
        Assert.Empty(states);
        Assert.Equal(timer, Assert.Single(rules).OwnRules);
        Assert.Equal(new BoardStateChange(id, rules[0]), Assert.Single(changes));
        Assert.Equal(timer, board.State.OwnRules);

        // Turning them off goes to the model, which reports what is left.
        Assert.Equal([new OwnRule(3, OwnRuleKind.Timer)], await board.TurnOffOwnRulesAsync([new OwnRule(3, OwnRuleKind.Timer)], Cancel));
        Assert.Equal(OwnRules.Empty, rules[^1].OwnRules);

        // Only the next model's rules come through, and they are the board's.
        await registry.ReplaceAsync(next.Build(id, Vm201Model.Model), Cancel);
        var other = new OwnRules([new OwnRule(5, OwnRuleKind.Timer)]);
        old.Fake.SetOwnRules(timer);
        next.Fake.SetOwnRules(other);

        Assert.Equal(3, rules.Count);
        Assert.Equal(other, rules[2].OwnRules);
        Assert.Equal(other, board.State.OwnRules);
        Assert.Equal(3, changes.Count);
        Assert.False(old.Fake.HasListeners);
    }

    // The states after a power cut alone are no state either; they reach the registry with the board's id, and setting
    // one goes to the current model.
    [Fact]
    public async Task Should_PassOnPowerOnChanges_OfTheCurrentModelOnly_AndNotAsStates()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var ability = new PowerOnAbility([PowerOnState.Off, PowerOnState.On], perRelay: true);
        var old = new FakeBoardParts(new FakeRelayBoard(powerOn: ability));
        var next = new FakeBoardParts(new FakeRelayBoard(powerOn: ability));
        var board = await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        var states = new List<BoardState>();
        var powerOns = new List<BoardState>();
        var changes = new List<BoardStateChange>();
        board.StateChanged += (_, state) => states.Add(state);
        board.PowerOnChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.PowerOnChanged += (_, state) => powerOns.Add(state);
        registry.PowerOnChanged += (_, change) => changes.Add(change);

        await board.SetPowerOnAsync(3, PowerOnState.On, Cancel);
        Assert.Empty(states);
        Assert.Equal(PowerOnState.On, Assert.Single(powerOns).PowerOn!.For(3));
        Assert.Equal(new BoardStateChange(id, powerOns[0]), Assert.Single(changes));
        Assert.Equal(PowerOnState.On, board.State.PowerOn!.For(3));

        await registry.ReplaceAsync(next.Build(id, Vm201Model.Model), Cancel);
        old.Fake.SetPowerOn(new PowerOnStates(new PowerOnState?[8]));
        next.Fake.SetPowerOn(new PowerOnStates(Enumerable.Repeat<PowerOnState?>(PowerOnState.Off, 8)));

        Assert.Equal(2, powerOns.Count);
        Assert.Equal(PowerOnState.Off, powerOns[1].PowerOn!.For(1));
        Assert.Equal(2, changes.Count);
        Assert.False(old.Fake.HasListeners);
    }

    [Fact]
    public async Task Should_RunTheModel_AndTheNextOneAfterAReplacement()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();
        var next = new FakeBoardParts();
        old.Extra.ConnectionRunning = () => old.Connection.Running;
        next.Extra.ConnectionRunning = () => next.Connection.Running;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);

        // Nothing to run yet: it waits for a board, and only ends when stopped or when a board's run fails.
        Assert.False(run.IsCompleted);
        await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        await WaitUntilAsync(() => old.Connection.Running && old.Extra.Started);

        await registry.ReplaceAsync(next.Build(id, Vm204Model.Model), Cancel);
        await WaitUntilAsync(() => next.Connection.Running && next.Extra.Started);

        // What only the model has started before its connection. The old one stopped, what only it had too, and was
        // disposed once its run ended.
        Assert.True(old.Extra.StartedBeforeConnection);
        Assert.True(next.Extra.StartedBeforeConnection);
        Assert.False(old.Connection.Running);
        Assert.True(old.Extra.Stopped);
        await WaitUntilAsync(() => old.Connection.Disposed);
        Assert.False(next.Connection.Disposed);

        await stop.CancelAsync();
        await run;
        Assert.True(next.Extra.Stopped);
        Assert.False(next.Connection.Disposed);
        await registry.DisposeAsync();
        Assert.True(next.Connection.Disposed);
        Assert.False(next.Fake.HasListeners);
        Assert.False(next.Traffic.HasListeners);
    }

    // One board's run that fails, or ends cancelled while nothing stops it (e.g. a timeout), would leave that board
    // without a connection: the others stop and RunAsync fails, so the host stops the app and the container restarts it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_StopTheOtherBoards_AndFail_WhenOneBoardsRunEnds(bool cancelled)
    {
        await using var database = await DatabaseAsync();
        var first = await AddRowAsync(database, sortOrder: 1);
        var second = await AddRowAsync(database, sortOrder: 2);
        var registry = Registry(database);
        var failing = new FakeBoardParts();
        var other = new FakeBoardParts();
        await registry.ReplaceAsync(failing.Build(first), Cancel);
        await registry.ReplaceAsync(other.Build(second), Cancel);
        var run = registry.RunAsync(Cancel);
        await WaitUntilAsync(() => failing.Connection.Running && other.Connection.Running);

        Exception failure = cancelled ? new TaskCanceledException("A request timed out.") : new InvalidOperationException("The board's client broke.");
        failing.Connection.Fail(failure);

        // A cancelled run keeps no exception of its own: the registry says which board's ended, with what ended it.
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => run);
        if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.Contains($"Relay board {first}'s connection ended", thrown.Message, StringComparison.Ordinal);
            Assert.Same(failure, thrown.InnerException);
        }
        else
        {
            Assert.Same(failure, thrown);
        }

        Assert.False(other.Connection.Running);
        Assert.True(other.Extra.Stopped);
        Assert.True(failing.Extra.Stopped);
    }

    // Stopping the app is quiet, also for a board whose connection ends cancelled by the stop.
    [Fact]
    public async Task Should_StopQuietly_WhenTheAppStops()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        await registry.ReplaceAsync(parts.Build(id), Cancel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);
        await WaitUntilAsync(() => parts.Connection.Running);

        await stop.CancelAsync();

        await run;
        Assert.False(parts.Connection.Running);
        Assert.True(parts.Extra.Stopped);
    }

    [Fact]
    public async Task Should_DisposeTheOldModelAtOnce_WhenItIsNotRunning()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();

        await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        await registry.ReplaceAsync(new FakeBoardParts().Build(id, Vm204Model.Model), Cancel);

        Assert.True(old.Connection.Disposed);
        Assert.False(old.Extra.Started);
    }

    // A board without an address tries nothing, and starts once one is set; at the start of its run it takes the
    // address saved on its row since it was built.
    [Fact]
    public async Task Should_WaitForAnAddress_AndTakeTheOneSavedOnItsRow_WhenItsRunStarts()
    {
        var time = new ManualTimeProvider();
        await using var database = await DatabaseAsync(time: time);
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        parts.Connection.DefaultEndpoint = new BoardEndpoint("", 9760);
        var board = await registry.ReplaceAsync(parts.Build(id), Cancel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);
        try
        {
            // It looks again after each wait, and still finds no address.
            for (var check = 0; check < 2; check++)
            {
                await WaitUntilAsync(() => time.Waits(LiveBoard.AddressCheck));
                time.Advance(LiveBoard.AddressCheck);
            }

            await WaitUntilAsync(() => time.Waits(LiveBoard.AddressCheck));
            Assert.Equal(0, parts.Connection.Runs);
            Assert.False(parts.Extra.Started);

            board.SetEndpoint(new BoardEndpoint("10.0.0.7", 9760));
            time.Advance(LiveBoard.AddressCheck);

            await WaitUntilAsync(() => parts.Connection.Running);
            Assert.True(parts.Extra.Started);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }

        // The next run (a new model) takes the address saved on the row by then.
        await SaveAddressAsync(database, id, "10.0.0.8", 9761);
        var next = new FakeBoardParts();
        await registry.ReplaceAsync(next.Build(id), Cancel);
        Assert.Equal(new BoardEndpoint("192.168.1.50", 9760), next.Connection.Endpoint);
        using var again = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var second = registry.RunAsync(again.Token);
        await WaitUntilAsync(() => next.Connection.Running);
        Assert.Equal(new BoardEndpoint("10.0.0.8", 9761), next.Connection.Endpoint);
        await again.CancelAsync();
        await second;
    }

    // A board taken out, or the registry disposed, passes on no more analog values or own rules.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PassOnNoMoreAnalogOrOwnRulesChanges_WhenTheBoardIsRemovedOrTheRegistryDisposed(bool disposed)
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts(new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff));
        await registry.ReplaceAsync(parts.Build(id, Vm204Model.Model), Cancel);
        var analogs = new List<BoardStateChange>();
        var rules = new List<BoardStateChange>();
        registry.AnalogChanged += (_, change) => analogs.Add(change);
        registry.OwnRulesChanged += (_, change) => rules.Add(change);
        parts.Fake.SetAnalog(new AnalogReadings([512]));
        parts.Fake.SetOwnRules(new OwnRules([new OwnRule(3, OwnRuleKind.Timer)]));
        Assert.Single(analogs);
        Assert.Single(rules);

        if (disposed)
        {
            await registry.DisposeAsync();
        }
        else
        {
            Assert.True(await registry.RemoveAsync(id, Cancel));
        }

        parts.Fake.SetAnalog(new AnalogReadings([600]));
        parts.Fake.SetOwnRules(OwnRules.Empty);

        Assert.Single(analogs);
        Assert.Single(rules);
        Assert.False(parts.Fake.HasListeners);
    }

    // Disposing lets go of everything: the model's events and its connection.
    [Fact]
    public async Task Should_LetGoOfEverything_WhenDisposed()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        var board = await registry.ReplaceAsync(parts.Build(id), Cancel);

        await registry.DisposeAsync();
        parts.Fake.SetRelay(1, on: true);

        Assert.Empty(registry.All);
        Assert.Null(board.Active);
        Assert.False(parts.Fake.HasListeners);
        Assert.False(parts.Traffic.HasListeners);
        Assert.True(parts.Connection.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => board.ReplaceAsync(new FakeBoardParts().Build(id)));
    }

    // Decided in Issue #4: the settings' address and login (Vm201__Host and so on) are the first board's
    // only. Another board starts at the model's default port with no address and no login, until one is saved for it;
    // its other settings are the model's as for any board.
    [Fact]
    public async Task Should_GiveTheSettingsAddressAndLogin_ToTheFirstBoardOnly()
    {
        await using var database = await DatabaseAsync(new Dictionary<string, string?>
        {
            ["Vm201:Host"] = "10.0.0.1",
            ["Vm201:Port"] = "9999",
            ["Vm201:Username"] = "admin",
            ["Vm201:Password"] = "secret",
            ["Vm201:KeepAliveSeconds"] = "30",
        });
        var first = await AddRowAsync(database, sortOrder: 1);
        var saved = await AddRowAsync(database, sortOrder: 2);
        var none = await AddRowAsync(database, sortOrder: 3);
        await SaveAddressAsync(database, saved, "10.0.0.2", 9761);
        var registry = Registry(database);

        await registry.LoadAsync(Cancel);

        Assert.Equal([first, saved, none], registry.All.Select(b => b.BoardId));
        var (one, two, three) = (registry.Find(first)!, registry.Find(saved)!, registry.Find(none)!);
        Assert.Equal(new BoardEndpoint("10.0.0.1", 9999), one.Endpoint);
        Assert.Equal(("admin", "secret"), (one.Login.Username, one.Login.Password));
        Assert.Equal(new BoardEndpoint("10.0.0.2", 9761), two.Endpoint);
        Assert.Equal(new BoardEndpoint("", 9760), two.DefaultEndpoint);
        Assert.True(two.Login.IsNone);
        Assert.Equal(new BoardEndpoint("", 9760), three.Endpoint);
        Assert.True(three.DefaultLogin.IsNone);
        Assert.All(registry.All, b => Assert.Equal(30, b.Active!.Services.GetRequiredService<IOptions<Vm201Options>>().Value.KeepAliveSeconds));
    }

    // The setup guide's choice: a row made since start gets its live board, and choosing its model again changes nothing.
    [Fact]
    public async Task Should_AddTheRowsBoard_AndKeepItsModel_WhenChosenAgain()
    {
        await using var database = await DatabaseAsync(new Dictionary<string, string?> { ["Vm201:Host"] = "10.0.0.1" });
        var registry = Registry(database);
        await registry.LoadAsync(Cancel);
        var id = await AddRowAsync(database, name: "Garage");

        var board = await registry.ReplaceModelAsync(id, Vm201Model.Model, Cancel);
        var active = board.Active;
        Assert.Same(board, await registry.ReplaceModelAsync(id, Vm201Model.Model, Cancel));

        Assert.Same(active, board.Active);
        Assert.Equal(("Garage", Vm201Model.Model), (board.Name, board.Model));
        Assert.Equal(new BoardEndpoint("10.0.0.1", 9760), board.Endpoint);
        Assert.Equal(id, board.Active!.BoardId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceModelAsync(id + 1, Vm201Model.Model, Cancel));
    }

    // A board "Add a board" didn't finish is removed at the next start, with its rows and events, and gets no live board,
    // so nothing is sent to it. A finished board stays, also one without an address (finished without a connection).
    [Fact]
    public async Task Should_RemoveAnUnfinishedBoard_AtStart_WithItsRows_AndKeepTheFinishedOnes()
    {
        await using var database = await DatabaseAsync();
        var workshop = await AddRowAsync(database, type: "vm201", sortOrder: 1, name: "Workshop");
        var shed = await AddRowAsync(database, type: "vm204", sortOrder: 2, name: "Shed");
        await SaveAddressAsync(database, shed, "", 80);
        var garage = await AddRowAsync(database, type: "vm204", sortOrder: 3, name: "Garage", unfinished: true);
        foreach (var id in new[] { workshop, shed, garage })
        {
            await database.Services.SeedBoardAsync(id, Vm204Model.Model.Capabilities, Cancel);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, BoardId = garage, Actor = "john@example.com", Action = EventActions.BoardAddressChanged });
            await db.SaveChangesAsync(Cancel);
        }

        var registry = Registry(database);
        await registry.LoadAsync(Cancel);

        Assert.Equal([workshop, shed], registry.All.Select(b => b.BoardId));
        Assert.Equal([workshop, shed], registry.Shown.Select(b => b.BoardId));
        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal([workshop, shed], await check.Boards.OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(Cancel));
        Assert.False(await check.Relays.AnyAsync(r => r.BoardId == garage, Cancel));
        Assert.False(await check.Inputs.AnyAsync(i => i.BoardId == garage, Cancel));
        Assert.False(await check.AnalogInputs.AnyAsync(i => i.BoardId == garage, Cancel));
        Assert.False(await check.BoardRuntimes.AnyAsync(r => r.BoardId == garage, Cancel));
        Assert.False(await check.EventLog.AnyAsync(e => e.BoardId == garage, Cancel));
        Assert.Equal(await check.Relays.CountAsync(Cancel), await check.RelayRuntimes.CountAsync(Cancel));
        Assert.True(await check.Relays.AnyAsync(r => r.BoardId == shed, Cancel));
    }

    // "Add a board" makes the board unfinished: it runs, but isn't shown, and the next start removes it. Once finished it
    // is shown, and the next start keeps it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_AddABoardUnfinished_AndKeepIt_OnlyOnceFinished(bool finished)
    {
        await using var database = await DatabaseAsync();
        var workshop = await AddRowAsync(database, type: "vm201", sortOrder: 1, name: "Workshop");
        var registry = Registry(database);
        await registry.LoadAsync(Cancel);
        var told = 0;
        registry.BoardsChanged += (_, _) => told++;

        var garage = await registry.AddBoardAsync(Vm204Model.Model, "Garage", Cancel);

        Assert.True(garage.IsUnfinished);
        Assert.Same(garage, registry.Find(garage.BoardId));
        Assert.Equal([workshop], registry.Shown.Select(b => b.BoardId));
        Assert.False(registry.IsShown(garage.BoardId));
        Assert.True(registry.IsShown(workshop));
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.True(await db.Boards.Where(b => b.Id == garage.BoardId).Select(b => b.IsUnfinished).SingleAsync(Cancel));
        }

        if (finished)
        {
            told = 0;
            Assert.True(await registry.FinishAddingAsync(garage.BoardId, Cancel));
            Assert.False(garage.IsUnfinished);
            Assert.Equal([workshop, garage.BoardId], registry.Shown.Select(b => b.BoardId));
            Assert.True(registry.IsShown(garage.BoardId));
            Assert.Equal(1, told);
        }

        // The next start.
        await using var next = new BoardRegistry(
            database.Factory, database.Services, database.Services.GetRequiredService<IConfiguration>(), TimeProvider.System, NullLoggerFactory.Instance, BoardAddressLimit.None);
        await next.LoadAsync(Cancel);

        Assert.Equal(finished ? [workshop, garage.BoardId] : [workshop], next.All.Select(b => b.BoardId));
        Assert.All(next.All, b => Assert.False(b.IsUnfinished));
        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(finished, await check.Boards.AnyAsync(b => b.Id == garage.BoardId, Cancel));
        Assert.Equal(finished, await check.Relays.AnyAsync(r => r.BoardId == garage.BoardId, Cancel));
    }

    // A delete that fails at start is logged, the board runs (still unfinished, not shown), and the next start removes it.
    [Fact]
    public async Task Should_LogAFailedRemoval_AtStart_AndTryAgainAtTheNextStart()
    {
        await using var database = await DatabaseAsync();
        var workshop = await AddRowAsync(database, type: "vm201", sortOrder: 1, name: "Workshop");
        var garage = await AddRowAsync(database, type: "vm204", sortOrder: 2, name: "Garage", unfinished: true);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER keep_board BEFORE DELETE ON \"Board\" BEGIN SELECT RAISE(ABORT, 'kept'); END;", Cancel);
        }

        var logs = new ListLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        var configuration = database.Services.GetRequiredService<IConfiguration>();
        await using (var first = new BoardRegistry(database.Factory, database.Services, configuration, TimeProvider.System, loggers, BoardAddressLimit.None))
        {
            await first.LoadAsync(Cancel);

            Assert.Equal([workshop, garage], first.All.Select(b => b.BoardId));
            Assert.Equal([workshop], first.Shown.Select(b => b.BoardId));
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Line.Contains($"Relay board {garage},", StringComparison.Ordinal));
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER keep_board;", Cancel);
        }

        await using var next = new BoardRegistry(database.Factory, database.Services, configuration, TimeProvider.System, loggers, BoardAddressLimit.None);
        await next.LoadAsync(Cancel);

        Assert.Equal([workshop], next.All.Select(b => b.BoardId));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Line.Contains($"Relay board {garage} (Garage) removed at start", StringComparison.Ordinal));
        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await check.Boards.AnyAsync(b => b.Id == garage, Cancel));
    }

    [Fact]
    public async Task Should_NotFinishABoard_ThatIsGone()
    {
        await using var database = await DatabaseAsync();
        var registry = Registry(database);

        Assert.False(await registry.FinishAddingAsync(42, Cancel));
    }

    private static BoardRegistry Registry(TestDatabase database)
    {
        return database.Services.GetRequiredService<BoardRegistry>();
    }

    private static async Task<TestDatabase> DatabaseAsync(IDictionary<string, string?>? settings = null, TimeProvider? time = null)
    {
        var database = TestDatabase.Create(
            (services, configuration) =>
            {
                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                services.AddBoards(configuration);
            },
            settings);
        await database.Services.InitializeDatabaseAsync(Cancel);
        return database;
    }

    private static async Task<int> AddRowAsync(TestDatabase database, string type = "vm201", int sortOrder = 1, string name = "Board", bool unfinished = false)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = new Board { Name = name, Type = type, SortOrder = sortOrder, IsUnfinished = unfinished };
        db.Boards.Add(board);
        await db.SaveChangesAsync(Cancel);
        return board.Id;
    }

    private static async Task SaveAddressAsync(TestDatabase database, int boardId, string host, int port)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.SingleAsync(b => b.Id == boardId, Cancel);
        (board.Host, board.Port) = (host, port);
        await db.SaveChangesAsync(Cancel);
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Line)> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName)
        {
            return new ListLogger(_entries);
        }

        public void Dispose()
        {
        }

        private sealed class ListLogger : ILogger
        {
            private readonly ConcurrentQueue<(LogLevel Level, string Line)> _entries;

            public ListLogger(ConcurrentQueue<(LogLevel Level, string Line)> entries)
            {
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                _entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
