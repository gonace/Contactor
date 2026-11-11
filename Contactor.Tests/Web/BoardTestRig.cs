using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Contactor.Vm201;
using Contactor.Web.Features.Groups;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's board side against the simulator: a database file, the board connection, the event recorder and relay
/// control, on a clock the test moves by hand (timers still run in real time).
/// </summary>
internal sealed class BoardTestRig : IAsyncDisposable
{
    public static readonly DateTime Start = new(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly List<IHostedService> _started = [];

    private BoardTestRig(Vm201Simulator simulator, TestDatabase database, FixedTimeProvider time)
    {
        Simulator = simulator;
        Database = database;
        Time = time;
    }

    public Vm201Simulator Simulator { get; }

    public TestDatabase Database { get; }

    public FixedTimeProvider Time { get; }

    public Vm201Client Client => Database.Services.GetRequiredService<Vm201Client>();

    public RelayControl Control => Database.Services.GetRequiredService<RelayControl>();

    public SchedulerService Scheduler => Database.Services.GetRequiredService<SchedulerService>();

    public GroupSequencer Groups => Database.Services.GetRequiredService<GroupSequencer>();

    public GroupsHandler GroupSetup => Database.Services.GetRequiredService<GroupsHandler>();

    public static async Task<BoardTestRig> StartAsync(Func<Vm201Simulator, Task>? prepare = null)
    {
        var simulator = new Vm201Simulator();
        simulator.Start();
        if (prepare is not null)
        {
            await prepare(simulator);
        }

        var time = new FixedTimeProvider(new DateTimeOffset(Start));
        var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddLocalClock(configuration);
                services.AddSingleton<TimeProvider>(time);
                services.AddVm201(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
                services.AddSchedules(configuration);
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = "false",
            });
        var rig = new BoardTestRig(simulator, database, time);
        await database.Services.InitializeDatabaseAsync(TestContext.Current.CancellationToken);

        // The recorder first, so it sees the first status.
        var hosted = database.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hosted.OfType<BoardEventRecorder>().Cast<IHostedService>().Concat(hosted.OfType<Vm201HostedService>()))
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            rig._started.Add(service);
        }

        await WaitUntilAsync(() => rig.Client.State.IsConnected);
        return rig;
    }

    public async Task<List<EventLogEntry>> EventsAsync()
    {
        await using var db = await Database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return [.. db.EventLog.OrderBy(e => e.Id)];
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public async Task WaitForEventsAsync(Func<List<EventLogEntry>, bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition(await EventsAsync()))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The events did not arrive in time.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in Enumerable.Reverse(_started))
        {
            await service.StopAsync(CancellationToken.None);
        }

        await Database.DisposeAsync();
        await Simulator.DisposeAsync();
    }
}
