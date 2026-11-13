using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Waveshare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// A Waveshare board's catalogue entry and the event the app logs when it set the relays to normal mode, against the
/// simulator over loopback.
/// </summary>
public sealed class WaveshareRelayModesServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogTheReset_WithTheModesItFound()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeLinkage);
        await using var database = CreateDatabase(simulator, "waveshare-8b", resetRelayModes: true);
        await database.Services.InitializeDatabaseAsync(Cancel);

        await RunAsync(database, async () =>
        {
            await WaitUntilAsync(() => simulator.RelayModes.All(m => m == WaveshareApi.ModeNormal));
            await WaitUntilAsync(() => CountEventsAsync(database).GetAwaiter().GetResult() == 1);
        });

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = await db.EventLog.SingleAsync(Cancel);
        Assert.Equal(EventActors.System, entry.Actor);
        Assert.Equal(EventActions.BoardRelayModesReset, entry.Action);
        Assert.Null(entry.RelayId);
        Assert.Equal("""{"modes":[0,1,0,0,0,0,0,0]}""", entry.Details);
    }

    [Fact]
    public async Task Should_LogTheModesAsUnknown_WhenTheyCouldntBeRead()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        simulator.RefuseModeReads = true;
        await using var database = CreateDatabase(simulator, "waveshare-8b", resetRelayModes: true);
        await database.Services.InitializeDatabaseAsync(Cancel);

        await RunAsync(database, () => WaitUntilAsync(() => CountEventsAsync(database).GetAwaiter().GetResult() == 1));

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardRelayModesReset, Cancel);
        Assert.Equal("""{"modes":"unknown"}""", entry.Details);
    }

    [Fact]
    public async Task Should_LogARedEventOnce_WhenTheBoardDidntTakeTheModes()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        simulator.RefuseModeReads = true;
        simulator.RefuseModeWrites = true;
        await using var database = CreateDatabase(simulator, "waveshare-8b", resetRelayModes: true);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var client = database.Services.GetRequiredService<WaveshareClient>();

        await RunAsync(database, async () =>
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            await WaitUntilAsync(() => CountEventsAsync(database, EventActions.BoardRelayModesNotReset).GetAwaiter().GetResult() == 1);
        });

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardRelayModesNotReset, Cancel);
        Assert.Equal(EventActors.System, entry.Actor);
        Assert.Equal("""{"reason":"The board refused it: it failed (code 4)."}""", entry.Details);
        Assert.Equal(0, await CountEventsAsync(database));
    }

    [Fact]
    public async Task Should_LogNothing_WhenEveryRelayWasInNormalMode()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8C);
        simulator.Start();
        await using var database = CreateDatabase(simulator, "waveshare-8c", resetRelayModes: true);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var client = database.Services.GetRequiredService<WaveshareClient>();

        await RunAsync(database, () => WaitUntilAsync(() => client.State.IsConnected));

        Assert.Equal(0, await CountEventsAsync(database));
        Assert.Equal(0, await CountEventsAsync(database, EventActions.BoardRelayModesNotReset));
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.StartsWith("01 10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LeaveTheModesAlone_WhenTurnedOffInSettings()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeLinkage);
        await using var database = CreateDatabase(simulator, "waveshare-8b", resetRelayModes: false);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var client = database.Services.GetRequiredService<WaveshareClient>();

        await RunAsync(database, () => WaitUntilAsync(() => client.State.IsConnected));

        Assert.Equal(WaveshareApi.ModeLinkage, simulator.RelayModes[1]);
        Assert.Equal(0, await CountEventsAsync(database));
    }

    [Theory]
    [InlineData("waveshare-8", 8, 0)]
    [InlineData("waveshare-8b", 8, 8)]
    [InlineData("waveshare-30", 30, 0)]
    public async Task Should_RegisterOneBoard_AndSeedItsRelaysAndInputs(string type, int relays, int inputs)
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.All.Single(m => m.Id == type));
        simulator.Start();
        await using var database = CreateDatabase(simulator, type, resetRelayModes: true);

        var board = database.Services.GetRequiredService<IRelayBoard>();
        await database.Services.InitializeDatabaseAsync(Cancel);

        // One instance, so the client has exactly one subscriber passing its state on.
        Assert.Same(database.Services.GetRequiredService<WaveshareBoard>(), board);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(Enumerable.Range(1, relays), await db.Relays.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Cancel));
        Assert.Equal(inputs, await db.Inputs.CountAsync(Cancel));
    }

    private static TestDatabase CreateDatabase(WaveshareSimulator simulator, string type, bool resetRelayModes)
    {
        return TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Board:Type"] = type,
                ["Waveshare:Host"] = "127.0.0.1",
                ["Waveshare:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Waveshare:ResetRelayModes"] = resetRelayModes ? "true" : "false",
            });
    }

    // As the app starts them: the service listens before the connection runs.
    private static async Task RunAsync(TestDatabase database, Func<Task> whileRunning)
    {
        var hosted = database.Services.GetServices<IHostedService>().ToList();
        List<IHostedService> services = [.. hosted.OfType<WaveshareRelayModesService>(), .. hosted.OfType<BoardHostedService>()];
        foreach (var service in services)
        {
            await service.StartAsync(Cancel);
        }

        try
        {
            await whileRunning();
        }
        finally
        {
            foreach (var service in services)
            {
                await service.StopAsync(Cancel);
            }
        }
    }

    private static async Task<int> CountEventsAsync(TestDatabase database, string action = EventActions.BoardRelayModesReset)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.CountAsync(e => e.Action == action, Cancel);
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
