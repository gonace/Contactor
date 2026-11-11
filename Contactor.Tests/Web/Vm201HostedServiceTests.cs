using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// The web app's board connection against the simulator over loopback.
/// </summary>
public sealed class Vm201HostedServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_DisableBoardTimersOnceAndLogIt_WhenFirstConnected()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x0F);
        await using var database = CreateDatabase(simulator, disableBoardTimers: true);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var client = database.Services.GetRequiredService<Vm201Client>();
        var service = database.Services.GetServices<IHostedService>().OfType<Vm201HostedService>().Single();

        await service.StartAsync(Cancel);
        try
        {
            await WaitUntilAsync(() => simulator.TimerMask == 0);
            await WaitUntilAsync(() => CountEventsAsync(database).GetAwaiter().GetResult() == 1);

            // A new connection must not send it again.
            simulator.DropConnections();
            await WaitUntilAsync(() => simulator.ConnectionCount >= 2 && client.State.IsConnected);
            await simulator.SetTimerMaskAsync(0x01);
            await WaitUntilAsync(() => client.State.TimerMask == 0x01);
        }
        finally
        {
            await service.StopAsync(Cancel);
        }

        Assert.Equal(1, simulator.ReceivedCommands.Count(p => p.Command == Vm201Command.TimerDisable));
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var entry = await db.EventLog.SingleAsync(Cancel);
        Assert.Equal(EventActors.System, entry.Actor);
        Assert.Equal(EventActions.BoardTimersDisabled, entry.Action);
        Assert.Null(entry.RelayId);
        Assert.Equal("""{"mask":255}""", entry.Details);
    }

    [Fact]
    public async Task Should_LeaveBoardTimersAlone_WhenTurnedOffInSettings()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x0F);
        await using var database = CreateDatabase(simulator, disableBoardTimers: false);
        await database.Services.InitializeDatabaseAsync(Cancel);
        var client = database.Services.GetRequiredService<Vm201Client>();
        var service = database.Services.GetServices<IHostedService>().OfType<Vm201HostedService>().Single();

        await service.StartAsync(Cancel);
        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            await simulator.SetOutputsAsync(0x01);
            await WaitUntilAsync(() => client.State.Outputs == 0x01);
        }
        finally
        {
            await service.StopAsync(Cancel);
        }

        Assert.Equal(0x0F, simulator.TimerMask);
        Assert.Equal(0, await CountEventsAsync(database));
    }

    [Fact]
    public async Task Should_RegisterOneBoard_WithTheRelaysTheDatabaseHolds()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var database = CreateDatabase(simulator, disableBoardTimers: false);

        var board = database.Services.GetRequiredService<IRelayBoard>();

        // One instance, so the client has exactly one subscriber passing its state on.
        Assert.Same(database.Services.GetRequiredService<Vm201Board>(), board);
        Assert.Same(board, database.Services.GetRequiredService<IRelayBoard>());
        Assert.Equal(Relay.Count, board.Capabilities.RelayCount);
    }

    private static TestDatabase CreateDatabase(Vm201Simulator simulator, bool disableBoardTimers)
    {
        return TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddVm201(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = disableBoardTimers ? "true" : "false",
            });
    }

    private static async Task<int> CountEventsAsync(TestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.CountAsync(Cancel);
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
