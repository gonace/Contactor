using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// Turning relays on and off against the simulator, with a real database file and a fixed clock.
/// </summary>
public sealed class RelayControlTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    private readonly Vm201Simulator _simulator = new();
    private TestDatabase _database = null!;
    private BoardHostedService _board = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private Vm201Client Client => _database.Services.BoardService<Vm201Client>();

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    public async ValueTask InitializeAsync()
    {
        _simulator.Start();
        _database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = _simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = "false",
            });
        await _database.Services.InitializeDatabaseAsync(Cancel);
        await _database.Services.UseBoardAsync("vm201");
        _board = _database.Services.GetServices<IHostedService>().OfType<BoardHostedService>().Single();
        await _board.StartAsync(Cancel);
        await WaitUntilAsync(() => Client.State.IsConnected);
    }

    public async ValueTask DisposeAsync()
    {
        await _board.StopAsync(CancellationToken.None);
        await _database.DisposeAsync();
        await _simulator.DisposeAsync();
    }

    [Fact]
    public async Task Should_TurnOnAndRecordWhoAndWhenItTurnsOff()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);

        await WaitUntilAsync(() => Client.State.IsOn(3));
        Assert.Equal(Vm201Channel.Mask(3), _simulator.Outputs);
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(Now, runtime.OnSinceUtc);
        Assert.Equal(Jane, runtime.TurnedOnBy);
        Assert.Equal(Now.AddMinutes(Relay.DefaultMaxOnMinutes), runtime.AutoOffAtUtc);
        var entry = await db.EventLog.SingleAsync(Cancel);
        Assert.Equal((3, Jane, EventActions.On, Now), (entry.RelayId!.Value, entry.Actor, entry.Action, entry.TimestampUtc));
    }

    [Fact]
    public async Task Should_NotPlanAutoOff_WhenTheRelayHasNoLimit()
    {
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.Relays.SingleAsync(r => r.Id == 2, Cancel);
            relay.MaxOnMinutes = null;
            await db.SaveChangesAsync(Cancel);
        }

        await Control.TurnOnAsync(2, Jane, Cancel);

        await using var check = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
        Assert.Null(runtime.AutoOffAtUtc);
        Assert.Equal(Now, runtime.OnSinceUtc);
    }

    [Fact]
    public async Task Should_TurnOffAndForgetWhoTurnedItOn()
    {
        await Control.TurnOnAsync(1, Jane, Cancel);
        await WaitUntilAsync(() => Client.State.IsOn(1));

        await Control.TurnOffAsync(1, "john@example.com", Cancel);

        await WaitUntilAsync(() => !Client.State.IsOn(1));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 1, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
        var actions = await db.EventLog.OrderBy(e => e.Id).Select(e => new { e.Action, e.Actor }).ToListAsync(Cancel);
        Assert.Equal([(EventActions.On, Jane), (EventActions.Off, "john@example.com")], actions.Select(a => (a.Action, a.Actor)));
    }

    [Fact]
    public async Task Should_TurnAllOffAndLogOnlyThoseThatWereOn()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await _simulator.SetOutputsAsync(0b0010_0010); // Relay 6 turned on at the board itself.
        await WaitUntilAsync(() => Client.State.Outputs == 0b0010_0010);

        await Control.TurnAllOffAsync("john@example.com", Cancel);

        await WaitUntilAsync(() => Client.State.Outputs == 0);
        Assert.Equal(1, _simulator.ReceivedCommands.Count(p => p.Command == Vm201Command.Off));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var offs = await db.EventLog.Where(e => e.Action == EventActions.Off).OrderBy(e => e.RelayId).ToListAsync(Cancel);
        Assert.Equal([2, 6], offs.Select(e => e.RelayId!.Value));
        Assert.All(offs, e => Assert.Equal("john@example.com", e.Actor));
        Assert.All(await db.RelayRuntimes.ToListAsync(Cancel), r => Assert.Null(r.OnSinceUtc));
    }

    [Fact]
    public async Task Should_RecordNothing_WhenTheBoardIsNotConnected()
    {
        _simulator.RejectConnections = true;
        _simulator.DropConnections();
        await WaitUntilAsync(() => !Client.State.IsConnected);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOnAsync(1, Jane, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOffAsync(1, Jane, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnAllOffAsync(Jane, Cancel));

        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(0, await db.EventLog.CountAsync(Cancel));
        Assert.All(await db.RelayRuntimes.ToListAsync(Cancel), r => Assert.Null(r.OnSinceUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Should_RejectRelaysTheBoardDoesNotHave(int relayId)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Control.TurnOnAsync(relayId, Jane, Cancel));
        Assert.DoesNotContain(_simulator.ReceivedCommands, p => p.Command == Vm201Command.On);
    }

    [Fact]
    public async Task Should_TellOpenPages_WhenARelayChanges()
    {
        var notified = 0;
        _database.Services.GetRequiredService<RelayChanges>().Changed += (_, _) => Interlocked.Increment(ref notified);

        await Control.TurnOnAsync(1, Jane, Cancel);
        await Control.TurnOffAsync(1, Jane, Cancel);
        await Control.TurnAllOffAsync(Jane, Cancel);

        Assert.Equal(3, notified);
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
