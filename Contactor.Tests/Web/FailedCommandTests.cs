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
    private readonly FixedTimeProvider _time = new(Now);
    private TestDatabase _database = null!;
    private BoardEventRecorder _recorder = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    private AutoOffService AutoOff => _database.Services.GetRequiredService<AutoOffService>();

    private SchedulerService Scheduler => _database.Services.GetRequiredService<SchedulerService>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<IRelayBoard>(_board);
            services.AddSingleton<TimeProvider>(_time);
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

    // Jane was told her ON failed, but the board switched the relay on. Nobody guesses it was hers: it is on outside the
    // app, with the relay's own auto-off, so a fan heater doesn't run until someone notices.
    [Fact]
    public async Task Should_GiveARelayItsOwnAutoOff_WhenItCameOnButItsCommandFailed()
    {
        _board.SwitchSilentlyThenFail = true;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOnAsync(3, Jane, Cancel));

        // The read that follows a failed command shows the relay on.
        await _board.RefreshAsync(Cancel);

        var entry = await SingleEventAsync();
        Assert.Equal((3, EventActors.External, EventActions.On), (entry.RelayId!.Value, entry.Actor, entry.Action));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(((DateTime?)Now, (DateTime?)Now.AddMinutes(Relay.DefaultMaxOnMinutes), EventActors.External), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    // A failed OFF the board carried out anyway is an external change too.
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

    // A board that switches a relay per request can refuse some relays of "All off": a refused relay didn't go off, so it
    // isn't logged as off, its run stays, and the board still shows it on.
    [Fact]
    public async Task Should_NotLogARefusedRelay_AsOff_InAllOff()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(2);

        _board.Refuse = ChannelMask.Of(3);
        await Control.TurnAllOffAsync(Jane, Cancel);
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(3);
        Assert.Equal([(2, EventActions.On), (3, EventActions.On), (2, EventActions.Off)], events.Select(e => (e.RelayId!.Value, e.Action)));
        Assert.True(_board.State.IsOn(3));
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            var run = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            Assert.Equal(Jane, run.TurnedOnBy);
            Assert.NotNull(run.OnSinceUtc);
        }

        await AssertNoExternalEventAsync();
    }

    // A relay the board refused in a group's step isn't the app's change: when it does go off later, it is logged as
    // changed outside the app.
    [Fact]
    public async Task Should_LogARelayRefusedInAGroupStop_AsExternal_WhenItGoesOffLater()
    {
        await AddGroupAsync((1, "Fan"), (2, "Heat"));
        await AddGroupAsync((3, "Fan"), (4, "Heat"));
        foreach (var relay in new[] { 1, 2, 3, 4 })
        {
            _board.SetRelay(relay, on: true);
        }

        await WaitForEventsAsync(4);
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();

        _board.Refuse = ChannelMask.Of(4);
        await sequencer.StopAllAsync(Jane, Cancel);
        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(4)));
        _board.Refuse = ChannelMask.None;
        _board.SetRelay(4, on: false);

        var events = await WaitForEventsAsync(e => e.Any(x => x is { RelayId: 4, Action: EventActions.Off }));
        Assert.Equal(EventActors.External, events.Single(e => e is { RelayId: 4, Action: EventActions.Off }).Actor);
    }

    // An auto-off the board refuses changes nothing: it isn't logged, the run stays, and the next check tries again.
    [Fact]
    public async Task Should_KeepTheRun_WhenTheBoardRefusesAnAutoOff()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        _board.Refuse = ChannelMask.Of(3);
        Assert.Empty(await AutoOff.CheckAsync(Cancel));

        Assert.True(_board.State.IsOn(3));
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.Equal(Jane, (await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel)).TurnedOnBy);
        }

        _board.Refuse = ChannelMask.None;
        Assert.Equal([3], await AutoOff.CheckAsync(Cancel));
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

    [Fact]
    public async Task Should_LogExternal_WhenAnAutoOffSwitchedButFailed()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        _board.SwitchSilentlyThenFail = true;
        Assert.Empty(await AutoOff.CheckAsync(Cancel));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(2);
        Assert.Equal([(Jane, EventActions.On), (EventActors.External, EventActions.Off)], events.Select(e => (e.Actor, e.Action)));
    }

    [Fact]
    public async Task Should_LogAnAutoOffAsItsOwn_WhenItsRetrySucceeds()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        _board.FailNextOff = true;
        Assert.Empty(await AutoOff.CheckAsync(Cancel));
        Assert.True(_board.State.IsOn(3));
        Assert.Equal([3], await AutoOff.CheckAsync(Cancel));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(2);
        Assert.Equal([(Jane, EventActions.On), (EventActors.AutoOff, EventActions.Off)], events.Select(e => (e.Actor, e.Action)));
        await AssertNoExternalEventAsync();
    }

    [Fact]
    public async Task Should_LogExternal_WhenAScheduleSwitchedButFailed()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;

        _board.SwitchSilentlyThenFail = true;
        await Scheduler.TickAsync(Cancel);
        await _board.RefreshAsync(Cancel);

        var entry = await SingleEventAsync();
        Assert.Equal((2, EventActors.External, EventActions.On), (entry.RelayId!.Value, entry.Actor, entry.Action));
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            // On outside the app, with its own auto-off for now.
            Assert.Equal(EventActors.External, (await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel)).TurnedOnBy);
        }

        // The next tick in its window takes it over: it is the schedule's, off at the OffTime.
        _time.Now = OnTime.AddMinutes(1);
        await Scheduler.TickAsync(Cancel);
        await using var check = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));
    }

    // The schedule turns a relay off at its OffTime, and someone turns it on again before the scheduler saves: their
    // new run stays.
    [Fact]
    public async Task Should_KeepANewRun_WhenTheScheduleEndsTheOldOneMeanwhile()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        _time.Now = OffTime;
        _board.AfterSwitch = async () =>
        {
            _board.AfterSwitch = null;
            _time.Now += TimeSpan.FromSeconds(1);
            await Control.TurnOnAsync(2, Jane, Cancel);
        };

        await Scheduler.TickAsync(Cancel);

        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
        Assert.Equal((Jane, (DateTime?)_time.Now.UtcDateTime), (runtime.TurnedOnBy, runtime.OnSinceUtc));
        Assert.True(_board.State.IsOn(2));
    }

    // A schedule's run starts when its ON went out, not at the minute it belongs to: a person's OFF sent in between
    // is told apart from it by time (RelayRuns.End), so a late ON keeps its run.
    [Fact]
    public async Task Should_StartAScheduleRun_WhenItsOnWentOut()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime.AddSeconds(20);

        await Scheduler.TickAsync(Cancel);

        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
        Assert.Equal((EventActors.Schedule, (DateTime?)OnTime.AddSeconds(20).UtcDateTime), (runtime.TurnedOnBy, runtime.OnSinceUtc));
    }

    // Auto-off turns a relay off, and someone turns it on again before auto-off saves: their new run stays.
    [Fact]
    public async Task Should_KeepANewRun_WhenAutoOffEndsTheOldOneMeanwhile()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes + 1);
        _board.AfterSwitch = async () =>
        {
            _board.AfterSwitch = null;
            _time.Now += TimeSpan.FromSeconds(1);
            await Control.TurnOnAsync(3, "john@example.com", Cancel);
        };

        await AutoOff.CheckAsync(Cancel);

        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(("john@example.com", (DateTime?)_time.Now.UtcDateTime), (runtime.TurnedOnBy, runtime.OnSinceUtc));
        Assert.True(_board.State.IsOn(3));
    }

    // The board confirms a schedule's ON before the scheduler has saved its run, and auto-off's check sees the relay on
    // with nobody's run in between: the schedule's run wins, with no auto-off, so it isn't turned off mid-window.
    [Fact]
    public async Task Should_KeepAScheduleRunFree_OfAnAutoOffGivenMeanwhile()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        var autoOff = _database.Services.GetRequiredService<AutoOffService>();
        _board.AfterSwitch = async () =>
        {
            _board.AfterSwitch = null;
            await autoOff.CheckAsync(Cancel);
        };

        await Scheduler.TickAsync(Cancel);

        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
        Assert.Equal((EventActors.Schedule, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));
    }

    [Fact]
    public async Task Should_LogAScheduleAsItsOwn_WhenItsRetrySucceeds()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        await WaitForEventsAsync(1);

        // The OFF fails and the minute isn't done: the next tick sends it again.
        _time.Now = OffTime;
        _board.FailNextOff = true;
        await Scheduler.TickAsync(Cancel);
        Assert.True(_board.State.IsOn(2));
        await Scheduler.TickAsync(Cancel);
        Assert.False(_board.State.IsOn(2));
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(2);
        Assert.Equal([(EventActors.Schedule, EventActions.On), (EventActors.Schedule, EventActions.Off)], events.Select(e => (e.Actor, e.Action)));
        await AssertNoExternalEventAsync();
    }

    // 14:05 and 14:10 local on the test's day; it starts at 14:02.
    private static DateTimeOffset OnTime => new(2026, 11, 4, 13, 5, 0, TimeSpan.Zero);

    private static DateTimeOffset OffTime => new(2026, 11, 4, 13, 10, 0, TimeSpan.Zero);

    private async Task AddScheduleAsync(int relayId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(14, 5),
            OffTime = new TimeOnly(14, 10),
            IsEnabled = true,
            CreatedBy = Jane,
        });
        await db.SaveChangesAsync(Cancel);
    }

    // The recorder handles each status in order, so once a later status is logged, an earlier one would be too.
    private async Task AssertNoExternalEventAsync()
    {
        _board.SetInput(1, true);
        var events = await WaitForEventsAsync(e => e.Any(x => x.Action == EventActions.InputChanged));
        Assert.DoesNotContain(events, e => e.Actor == EventActors.External && e.Action != EventActions.InputChanged);
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
