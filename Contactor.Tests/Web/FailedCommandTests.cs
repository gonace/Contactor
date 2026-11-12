using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// A command the board carried out but whose answer was lost or unreadable: the app logged nothing, so the change the
/// next status shows is logged as external, not taken for the app's own and lost.
/// </summary>
public sealed class FailedCommandTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    private readonly FakeRelayBoard _board = new(4);
    private TestDatabase _database = null!;
    private BoardEventRecorder _recorder = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<IRelayBoard>(_board);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSchedules(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
        _recorder = _database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await _recorder.StartAsync(Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _recorder.StopAsync(CancellationToken.None);
        await _database.DisposeAsync();
    }

    // Jane was told her ON failed, but the board switched the relay on: it is her ON, with her auto-off, so a fan
    // heater doesn't run until someone notices.
    [Fact]
    public async Task Should_CreditThePerson_WhenARelayCameOnButItsCommandFailed()
    {
        _board.SwitchSilentlyThenFail = true;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOnAsync(3, Jane, Cancel));

        // The read that follows a failed command shows the relay on.
        await _board.RefreshAsync(Cancel);

        var entry = await SingleEventAsync();
        Assert.Equal((3, Jane, EventActions.On, """{"afterFailedCommand":true}"""), (entry.RelayId!.Value, entry.Actor, entry.Action, entry.Details));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(((DateTime?)Now, (DateTime?)Now.AddMinutes(Relay.DefaultMaxOnMinutes), Jane), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    // Only an ON is credited: a failed OFF the board carried out anyway is still an external change.
    [Fact]
    public async Task Should_LogExternal_WhenARelayWentOffButItsCommandFailed()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);

        _board.SwitchSilentlyThenFail = true;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOffAsync(3, Jane, Cancel));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(2);
        Assert.Equal((EventActors.External, EventActions.Off), (events[1].Actor, events[1].Action));
    }

    [Fact]
    public async Task Should_LogExternal_WhenAllOffSwitchedButFailed()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await WaitForEventsAsync(1);

        _board.SwitchSilentlyThenFail = true;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnAllOffAsync(Jane, Cancel));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(2);
        Assert.Equal([(Jane, EventActions.On), (EventActors.External, EventActions.Off)], events.Select(e => (e.Actor, e.Action)));
    }

    [Fact]
    public async Task Should_LogTheAppsOwnChange_WhenTheCommandSucceeded()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await _board.RefreshAsync(Cancel);

        var entry = await SingleEventAsync();
        Assert.Equal((3, Jane, EventActions.On), (entry.RelayId!.Value, entry.Actor, entry.Action));
    }

    [Fact]
    public async Task Should_LogExternal_WhenAGroupStepSwitchedButFailed()
    {
        var groupId = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();

        _board.SwitchSilentlyThenFail = true;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => sequencer.StartGroupAsync(groupId, Jane, Cancel));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(e => e.Any(x => x.RelayId == 1));
        var entry = Assert.Single(events, e => e.RelayId == 1);
        Assert.Equal((EventActors.External, EventActions.On), (entry.Actor, entry.Action));
    }

    private async Task<int> AddGroupAsync(params (int RelayId, string Role)[] members)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
        for (var i = 0; i < members.Length; i++)
        {
            group.Members.Add(new RelayGroupMember { RelayId = members[i].RelayId, Position = i + 1, RoleName = members[i].Role, StartDelaySec = 3 });
        }

        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }

    private async Task<EventLogEntry> SingleEventAsync()
    {
        return Assert.Single(await WaitForEventsAsync(1));
    }

    private Task<List<EventLogEntry>> WaitForEventsAsync(int count)
    {
        return WaitForEventsAsync(events => events.Count >= count);
    }

    // Waits for the recorder's own loop to write them; never asserts on how long that takes.
    private async Task<List<EventLogEntry>> WaitForEventsAsync(Func<List<EventLogEntry>, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
            {
                var events = await db.EventLog.OrderBy(e => e.Id).ToListAsync(Cancel);
                if (condition(events))
                {
                    return events;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The events were not logged.");
            }

            await Task.Delay(10, Cancel);
        }
    }
}
