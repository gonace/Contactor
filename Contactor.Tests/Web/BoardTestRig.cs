using Contactor.Boards.Vm201;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's board side against the simulator: a database file, the board connection, the event recorder and relay
/// control, on a clock the test moves by hand (timers still run in real time). With a second board, the simulator is
/// the first board and a board in memory (<see cref="Second"/>, 4 relays and 2 inputs) the second, each with its own
/// rows, so the second board's relays' ids are never its channels.
/// </summary>
internal sealed class BoardTestRig : IAsyncDisposable
{
    public static readonly DateTime Start = new(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly List<IHostedService> _started = [];
    private readonly Dictionary<int, int> _secondRelays = [];
    private readonly Dictionary<int, int> _secondInputs = [];

    private BoardTestRig(Vm201Simulator simulator, TestDatabase database, FixedTimeProvider time, RelayLayout layout)
    {
        Simulator = simulator;
        Database = database;
        Time = time;
        Layout = layout;
    }

    /// <summary>How the database numbers the relays and inputs.</summary>
    public RelayLayout Layout { get; }

    public Vm201Simulator Simulator { get; }

    public TestDatabase Database { get; }

    public FixedTimeProvider Time { get; }

    public Vm201Client Client => Live.Active!.Services.GetRequiredService<Vm201Client>();

    /// <summary>The first board: the simulator.</summary>
    public LiveBoard Live => Database.Services.GetRequiredService<BoardRegistry>().All[0];

    /// <summary>The second board, in memory; null without one.</summary>
    public FakeRelayBoard? Second { get; private set; }

    /// <summary>The second board's row.</summary>
    public int SecondBoardId { get; private set; }

    /// <summary>The second board as the app runs it.</summary>
    public LiveBoard SecondLive => Database.Services.GetRequiredService<BoardRegistry>().Find(SecondBoardId)!;

    /// <summary>The id of the relay on this channel of the second board.</summary>
    public int SecondRelayId(int channel)
    {
        return _secondRelays[channel];
    }

    /// <summary>The id of the input on this channel of the second board.</summary>
    public int SecondInputId(int channel)
    {
        return _secondInputs[channel];
    }

    public RelayControl Control => Database.Services.GetRequiredService<RelayControl>();

    public SchedulerService Scheduler => Database.Services.GetRequiredService<SchedulerService>();

    public GroupSequencer Groups => Database.Services.GetRequiredService<GroupSequencer>();

    public GroupsHandler GroupSetup => Database.Services.GetRequiredService<GroupsHandler>();

    /// <summary>The id of the relay on this channel of the simulated board.</summary>
    public int RelayId(int channel)
    {
        return RelayLayouts.RelayId(Layout, channel, Simulator.Model.Capabilities.RelayCount);
    }

    /// <summary>The id of the input on this channel of the simulated board.</summary>
    public int InputId(int channel)
    {
        return RelayLayouts.InputId(Layout, channel, Simulator.Model.Capabilities.InputCount);
    }

    public static async Task<BoardTestRig> StartAsync(
        Func<Vm201Simulator, Task>? prepare = null,
        ILoggerProvider? logs = null,
        RelayLayout layout = RelayLayout.Same,
        bool secondBoard = false)
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
                services.AddBoards(configuration);
                services.AddRelays(configuration);
                services.AddGroups(configuration);
                services.AddEvents(configuration);
                services.AddInputs(configuration);
                services.AddSchedules(configuration);
                if (logs is not null)
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                }
            },
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = "false",
            });
        var rig = new BoardTestRig(simulator, database, time, layout);
        await database.Services.InitializeDatabaseAsync(TestContext.Current.CancellationToken);
        await RelayLayouts.SeedAsync(database.Services, layout, simulator.Model.Capabilities.RelayCount, simulator.Model.Capabilities.InputCount);
        await database.Services.UseBoardAsync("vm201");
        if (secondBoard)
        {
            await rig.AddSecondBoardAsync();
        }

        // The recorder first, so it sees the first status.
        var hosted = database.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hosted.OfType<BoardEventRecorder>().Cast<IHostedService>().Concat(hosted.OfType<BoardHostedService>()))
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            rig._started.Add(service);
        }

        await WaitUntilAsync(() => rig.Client.State.IsConnected);
        return rig;
    }

    // A second board's row after the first, its relays and inputs, and a board in memory on it.
    private async Task AddSecondBoardAsync()
    {
        var cancel = TestContext.Current.CancellationToken;
        Second = new FakeRelayBoard(relayCount: 4, inputCount: 2) { Time = Time };
        await using (var db = await Database.Factory.CreateDbContextAsync(cancel))
        {
            var row = new Board { Name = "Barn", SortOrder = 2 };
            db.Boards.Add(row);
            await db.SaveChangesAsync(cancel);
            SecondBoardId = row.Id;
        }

        await Database.Services.SeedBoardAsync(SecondBoardId, relayCount: 4, inputCount: 2, cancel);
        await using (var db = await Database.Factory.CreateDbContextAsync(cancel))
        {
            foreach (var relay in db.Relays.Where(r => r.BoardId == SecondBoardId))
            {
                _secondRelays[relay.Channel] = relay.Id;
            }

            foreach (var input in db.Inputs.Where(i => i.BoardId == SecondBoardId))
            {
                _secondInputs[input.Channel] = input.Id;
            }
        }

        await Database.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(Second).Build(SecondBoardId), cancel);
    }

    /// <summary>Starts one of the app's background services, e.g. the group sequencer's loops; stopped on dispose.</summary>
    public async Task<T> StartServiceAsync<T>()
        where T : IHostedService
    {
        var service = Database.Services.GetServices<IHostedService>().OfType<T>().Single();
        await service.StartAsync(TestContext.Current.CancellationToken);
        _started.Add(service);
        return service;
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
