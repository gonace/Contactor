using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Groups;
using Contactor.Web;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Web;

public sealed class BoardEventRecorderTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogNothing_ForTheFirstStatusAfterStart()
    {
        await using var rig = await BoardTestRig.StartAsync(s => s.SetOutputsAsync(0b0000_0101));

        await rig.Simulator.SetOutputsAsync(0b0000_0101);
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.Empty(await rig.EventsAsync());
    }

    [Fact]
    public async Task Should_LogExternal_WhenARelayIsTurnedOnAtTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(4));

        await rig.WaitForEventsAsync(events => events.Count == 1);
        var entry = Assert.Single(await rig.EventsAsync());
        Assert.Equal((4, EventActors.External, EventActions.On), (entry.RelayId!.Value, entry.Actor, entry.Action));
    }

    [Fact]
    public async Task Should_NotLogTheAppsOwnCommandsTwice()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        await rig.Control.TurnAllOffAsync(Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0);
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        var events = await rig.EventsAsync();
        Assert.Equal([EventActions.On, EventActions.Off], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(Jane, e.Actor));
    }

    [Fact]
    public async Task Should_LogExternal_WhenTheExpectationHasExpired()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        // The app asked for "on" again while it was on; the board answers nothing, so that expectation lingers.
        await rig.Control.TurnOnAsync(2, Jane, Cancel);
        rig.Time.Now += ExpectedChanges.Lifetime;
        await rig.Simulator.SetOutputsAsync(0);
        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(2));

        await rig.WaitForEventsAsync(events => events.Count(e => e.Actor == EventActors.External) == 2);
    }

    [Fact]
    public async Task Should_ForgetWhoTurnedItOn_WhenItIsTurnedOffAtTheBoard()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        await rig.Simulator.SetOutputsAsync(0);

        await rig.WaitForEventsAsync(events => events.Any(e => e.Actor == EventActors.External));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    [Fact]
    public async Task Should_LogChangesMadeWhileDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync(s => s.SetOutputsAsync(Vm201Channel.Mask(1)));
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);

        await rig.Simulator.SetOutputsAsync(Vm201Channel.Mask(5));
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        await rig.WaitForEventsAsync(events => events.Count == 2);
        var events = await rig.EventsAsync();
        Assert.Contains(events, e => e is { RelayId: 1, Action: EventActions.Off, Actor: EventActors.External });
        Assert.Contains(events, e => e is { RelayId: 5, Action: EventActions.On, Actor: EventActors.External });
    }

    [Fact]
    public async Task Should_LogTheInput_WhenItChanges()
    {
        await using var rig = await BoardTestRig.StartAsync();

        await rig.Simulator.SetInputAsync(true);

        await rig.WaitForEventsAsync(events => events.Count == 1);
        var entry = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActions.InputChanged, """{"on":true}""", (int?)null, (int?)1), (entry.Action, entry.Details, entry.RelayId, entry.InputId));
    }

    // A board with several inputs, e.g. the VM204: each change is logged with its input.
    [Fact]
    public async Task Should_LogEveryInput_WithItsNumber()
    {
        var board = new Contactor.Tests.Boards.FakeRelayBoard(relayCount: 4, inputCount: 4);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddSingleton<Contactor.Boards.IRelayBoard>(board);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel, inputCount: 4);
        var recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            board.SetInput(3, on: true);
            board.SetInput(1, on: true);
            board.SetInput(3, on: false);

            await BoardTestRig.WaitUntilAsync(() => Events(database).Count == 3);
            Assert.Equal(
                [(3, """{"on":true}"""), (1, """{"on":true}"""), (3, """{"on":false}""")],
                Events(database).Select(e => (e.InputId ?? 0, e.Details ?? "")));
        }
        finally
        {
            await recorder.StopAsync(Cancel);
        }
    }

    private static List<EventLogEntry> Events(TestDatabase database)
    {
        using var db = database.Factory.CreateDbContext();
        return [.. db.EventLog.OrderBy(e => e.Id)];
    }

    [Fact]
    public async Task Should_LogLossAfter30SecondsAndTheReturn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        var lostAt = rig.Time.Now.UtcDateTime;

        rig.Time.Now += BoardEventRecorder.ConnectionLostAfter - TimeSpan.FromSeconds(1);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        Assert.Empty(await rig.EventsAsync());

        rig.Time.Now += TimeSpan.FromSeconds(1);
        await rig.WaitForEventsAsync(events => events.Count == 1);
        var lost = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActions.ConnectionLost, EventActors.System, lostAt), (lost.Action, lost.Actor, lost.TimestampUtc));

        rig.Simulator.RejectConnections = false;
        await rig.WaitForEventsAsync(events => events.Count == 2);
        var restored = (await rig.EventsAsync())[1];
        Assert.Equal(EventActions.ConnectionRestored, restored.Action);
        Assert.Equal("""{"afterSeconds":30}""", restored.Details);
    }

    [Fact]
    public async Task Should_LogNothing_WhenTheConnectionComesBackQuickly()
    {
        await using var rig = await BoardTestRig.StartAsync();
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);

        rig.Time.Now += BoardEventRecorder.ConnectionLostAfter * 2;
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);

        Assert.Empty(await rig.EventsAsync());
    }
}
