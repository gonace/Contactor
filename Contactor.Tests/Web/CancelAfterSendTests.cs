using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The caller's token is cancelled right after a command went out, mostly at shutdown: the board took the command, so
/// its run, its auto-off and its event are still saved. Otherwise the board's change would count as the app's own with
/// no run and no auto-off.
/// </summary>
public sealed class CancelAfterSendTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    private readonly FakeRelayBoard _board = new(4, pulse: new PulseAbility(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)))
    {
        ThrowWhenCancelled = true,
    };

    private readonly FixedTimeProvider _time = new(Now);
    private readonly CancellationTokenSource _caller = new();
    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    private GroupSequencer Groups => _database.Services.GetRequiredService<GroupSequencer>();

    private AutoOffService AutoOff => _database.Services.GetRequiredService<AutoOffService>();

    private SchedulerService Scheduler => _database.Services.GetRequiredService<SchedulerService>();

    private int BoardId => _database.Services.Live().BoardId;

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddBoards(configuration);
            services.AddSingleton<TimeProvider>(_time);
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSchedules(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
        await _database.Services.UseFakeBoardAsync(_board);
    }

    public async ValueTask DisposeAsync()
    {
        _caller.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_RecordAnOn_WhenTheCallerCancelsAfterItWentOut()
    {
        CancelAfterTheNextSwitch();

        await Control.TurnOnAsync(3, Jane, _caller.Token);

        var run = await RunAsync(3);
        Assert.Equal((Jane, (DateTime?)Now, (DateTime?)Now.AddMinutes(Relay.DefaultMaxOnMinutes)), (run.TurnedOnBy, run.OnSinceUtc, run.AutoOffAtUtc));
        Assert.Equal([(3, Jane, EventActions.On)], await EventsAsync());
    }

    [Fact]
    public async Task Should_RecordAPulse_WhenTheCallerCancelsAfterItWentOut()
    {
        CancelAfterTheNextSwitch();

        await Control.PulseAsync(3, TimeSpan.FromSeconds(5), Jane, _caller.Token);

        var run = await RunAsync(3);
        Assert.Equal((Jane, (DateTime?)Now.AddSeconds(5) + RelayControl.PulseBackstop), (run.TurnedOnBy, run.AutoOffAtUtc));
        Assert.Equal([(3, Jane, EventActions.Pulse)], await EventsAsync());
    }

    [Fact]
    public async Task Should_RecordAnOff_WhenTheCallerCancelsAfterItWentOut()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        CancelAfterTheNextSwitch();

        await Control.TurnOffAsync(3, Jane, _caller.Token);

        Assert.Null((await RunAsync(3)).OnSinceUtc);
        Assert.Equal([(3, Jane, EventActions.On), (3, Jane, EventActions.Off)], await EventsAsync());
    }

    // The OFF that went out is recorded, and the groups stop all the same, with their run-on: "All off" doesn't end
    // with the relays outside a group off and a group still running.
    [Fact]
    public async Task Should_RecordAllOffAndStopTheGroups_WhenTheCallerCancelsAfterItWentOut()
    {
        var groupId = await AddGroupAsync((3, "Fan"), (4, "Heat"));
        await Groups.StartGroupAsync(groupId, Jane, Cancel);
        await Groups.StepAsync(Cancel);
        _time.Now += TimeSpan.FromSeconds(3);
        await Groups.StepAsync(Cancel);
        Assert.True(_board.State.IsOn(4));
        await Control.TurnOnAsync(2, Jane, Cancel);
        CancelAfterTheNextSwitch();

        await Control.TurnAllOffAsync(BoardId, Jane, _caller.Token);

        Assert.True(_caller.IsCancellationRequested);
        Assert.False(_board.State.IsOn(2));
        Assert.False(_board.State.IsOn(4));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal(GroupPhases.Stopping, (await GroupRunAsync(groupId)).Phase);
        var events = await EventsAsync();
        Assert.Contains((2, Jane, EventActions.Off), events);
        Assert.Contains((0, Jane, EventActions.GroupOff), events);
    }

    [Fact]
    public async Task Should_RecordAGroupStep_WhenTheCallerCancelsAfterItWentOut()
    {
        var groupId = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        CancelAfterTheNextSwitch();

        await Groups.StartGroupAsync(groupId, Jane, _caller.Token);

        Assert.True(_board.State.IsOn(1));
        var runtime = await GroupRunAsync(groupId);
        Assert.Equal((GroupPhases.Starting, Jane, (DateTime?)Now.AddMinutes(RelayGroup.DefaultMaxOnMinutes)), (runtime.Phase, runtime.TurnedOnBy, runtime.AutoOffAtUtc));
        Assert.Contains((0, Jane, EventActions.GroupOn), await EventsAsync());
    }

    // One group's stop and another group's next ON go in the same step: the OFF went out, the caller cancels before the
    // ON, and the stop is still recorded.
    [Fact]
    public async Task Should_RecordAGroupsStop_WhenTheCallerCancelsBeforeTheOnOfTheSameStep()
    {
        var first = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        var second = await AddGroupAsync((3, "Fan"), (4, "Heat"));
        await Groups.StartGroupAsync(first, Jane, Cancel);
        await Groups.StepAsync(Cancel);
        _time.Now += TimeSpan.FromSeconds(3);
        await Groups.StepAsync(Cancel);
        Assert.True(_board.State.IsOn(2));
        await Groups.StartGroupAsync(second, Jane, Cancel);
        await Groups.StepAsync(Cancel);

        // The second group's heat is due when the first group stops.
        _time.Now += TimeSpan.FromSeconds(3);
        _board.AfterOff = () =>
        {
            _board.AfterOff = null;
            _caller.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Groups.StopGroupAsync(first, Jane, _caller.Token));

        Assert.False(_board.State.IsOn(2));
        Assert.False(_board.State.IsOn(4));
        Assert.Equal(GroupPhases.Stopping, (await GroupRunAsync(first)).Phase);
        Assert.Contains((0, Jane, EventActions.GroupOff), await EventsAsync());
    }

    [Fact]
    public async Task Should_RecordAnAutoOff_WhenTheCallerCancelsAfterItWentOut()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        CancelAfterTheNextSwitch();

        Assert.Equal([3], await AutoOff.CheckAsync(_caller.Token));

        Assert.Null((await RunAsync(3)).OnSinceUtc);
        Assert.Equal([(3, Jane, EventActions.On), (3, EventActors.AutoOff, EventActions.Off)], await EventsAsync());
    }

    // The next relay's OFF doesn't go out once the caller cancelled; the one that went out is recorded.
    [Fact]
    public async Task Should_RecordTheAutoOffsThatWentOut_WhenTheCallerCancelsBeforeTheNext()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await Control.TurnOnAsync(3, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        CancelAfterTheNextSwitch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoOff.CheckAsync(_database.Services.Live(), _caller.Token));

        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(3)));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal(Jane, (await RunAsync(3)).TurnedOnBy);
        Assert.Equal([(2, Jane, EventActions.On), (3, Jane, EventActions.On), (2, EventActors.AutoOff, EventActions.Off)], await EventsAsync());
    }

    // The groups' part of the tick that follows is cancelled; the relay's ON that went out is recorded.
    [Fact]
    public async Task Should_RecordAScheduleOn_WhenTheCallerCancelsAfterItWentOut()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        CancelAfterTheNextSwitch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        var run = await RunAsync(2);
        Assert.Equal((EventActors.Schedule, (DateTime?)OnTime.UtcDateTime, (DateTime?)null), (run.TurnedOnBy, run.OnSinceUtc, run.AutoOffAtUtc));
        Assert.Equal([(2, EventActors.Schedule, EventActions.On)], await EventsAsync());
    }

    [Fact]
    public async Task Should_RecordAScheduleOff_WhenTheCallerCancelsAfterItWentOut()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        _time.Now = OffTime;
        CancelAfterTheNextSwitch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal([(2, EventActors.Schedule, EventActions.On), (2, EventActors.Schedule, EventActions.Off)], await EventsAsync());
    }

    // The next relay's ON doesn't go out once the caller cancelled; the one that went out is recorded.
    [Fact]
    public async Task Should_RecordTheScheduleOnsThatWentOut_WhenTheCallerCancelsBeforeTheNext()
    {
        await AddScheduleAsync(relayId: 2);
        await AddScheduleAsync(relayId: 3);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        CancelAfterTheNextSwitch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        Assert.Equal((true, false), (_board.State.IsOn(2), _board.State.IsOn(3)));
        Assert.Equal(EventActors.Schedule, (await RunAsync(2)).TurnedOnBy);
        Assert.Equal([(2, EventActors.Schedule, EventActions.On)], await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(3, on: true));

        // The next tick, without a cancel, turns the other relay on.
        await Scheduler.TickAsync(Cancel);
        Assert.True(_board.State.IsOn(3));
    }

    // The next relay's OFF doesn't go out once the caller cancelled; the one that went out is recorded.
    [Fact]
    public async Task Should_RecordTheScheduleOffsThatWentOut_WhenTheCallerCancelsBeforeTheNext()
    {
        await AddScheduleAsync(relayId: 2);
        await AddScheduleAsync(relayId: 3);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        _time.Now = OffTime;
        CancelAfterTheNextSwitch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(3)));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal(EventActors.Schedule, (await RunAsync(3)).TurnedOnBy);
        Assert.Equal(
            [(2, EventActors.Schedule, EventActions.On), (3, EventActors.Schedule, EventActions.On), (2, EventActors.Schedule, EventActions.Off)],
            await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(3, on: false));

        // The next tick, without a cancel, turns the other relay off.
        await Scheduler.TickAsync(Cancel);
        Assert.False(_board.State.IsOn(3));
    }

    // A relay's ON went out, then the caller cancels while the groups' part of the tick runs, after the group's own ON
    // went out: both are recorded, and the minute is done again without switching or logging anything twice.
    [Fact]
    public async Task Should_RecordTheRelayAndTheGroup_WhenTheCallerCancelsDuringTheGroups()
    {
        await AddScheduleAsync(relayId: 4);
        var groupId = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        await AddScheduleAsync(relayId: null, groupId);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        var switches = 0;
        _board.AfterSwitch = () =>
        {
            if (++switches == 2)
            {
                _board.AfterSwitch = null;
                _caller.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        Assert.Equal((true, true), (_board.State.IsOn(4), _board.State.IsOn(1)));
        Assert.Equal(EventActors.Schedule, (await RunAsync(4)).TurnedOnBy);
        Assert.Equal((GroupPhases.Starting, EventActors.Schedule), ((await GroupRunAsync(groupId)).Phase, (await GroupRunAsync(groupId)).TurnedOnBy));
        var events = await EventsAsync();
        Assert.Contains((4, EventActors.Schedule, EventActions.On), events);
        Assert.Contains((0, EventActors.Schedule, EventActions.GroupOn), events);

        var sent = _board.Sent.Count;
        await Scheduler.TickAsync(Cancel);
        Assert.Equal(sent, _board.Sent.Count);
        Assert.Equal(events, await EventsAsync());
    }

    // A token cancelled before anything went out: nothing is sent, logged or expected.
    [Fact]
    public async Task Should_DoNothing_WhenAnOnIsCancelledBeforeItGoesOut()
    {
        await _caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Control.TurnOnAsync(3, Jane, _caller.Token));

        await AssertUntouchedAsync(3);
    }

    [Fact]
    public async Task Should_DoNothing_WhenAScheduleTickIsCancelledBeforeItsOn()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await _caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(_caller.Token));

        await AssertUntouchedAsync(2);
    }

    [Fact]
    public async Task Should_DoNothing_WhenAnAutoOffIsCancelledBeforeItsOff()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        await _caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoOff.CheckAsync(_caller.Token));

        Assert.True(_board.State.IsOn(3));
        Assert.Single(_board.Sent);
        Assert.Equal([(3, Jane, EventActions.On)], await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(3, on: false));
        Assert.Equal(Jane, (await RunAsync(3)).TurnedOnBy);
    }

    // The board got no command, nothing is logged, no change is expected and the relay has no run.
    private async Task AssertUntouchedAsync(int relayId)
    {
        Assert.False(_board.State.IsOn(relayId));
        Assert.Empty(_board.Sent);
        Assert.Empty(await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(relayId, on: true));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await db.RelayRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.RelayId == relayId, Cancel))?.OnSinceUtc);
    }

    // 14:05 and 14:10 local on the test's day; it starts at 14:02.
    private static DateTimeOffset OnTime => new(2026, 11, 4, 13, 5, 0, TimeSpan.Zero);

    private static DateTimeOffset OffTime => new(2026, 11, 4, 13, 10, 0, TimeSpan.Zero);

    // The caller cancels once the board took the next command, before that command returns.
    private void CancelAfterTheNextSwitch()
    {
        _board.AfterSwitch = () =>
        {
            _board.AfterSwitch = null;
            _caller.Cancel();
            return Task.CompletedTask;
        };
    }

    private async Task<RelayRuntime> RunAsync(int relayId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    private async Task<GroupRuntime> GroupRunAsync(int groupId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.GroupRuntimes.AsNoTracking().SingleAsync(r => r.GroupId == groupId, Cancel);
    }

    // The app's own events, as (relay or 0, actor, action).
    private async Task<List<(int RelayId, string Actor, string Action)>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return [.. (await db.EventLog.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Cancel)).Select(e => (e.RelayId ?? 0, e.Actor, e.Action))];
    }

    private async Task AddScheduleAsync(int? relayId, int? groupId = null)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            GroupId = groupId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(14, 5),
            OffTime = new TimeOnly(14, 10),
            IsEnabled = true,
            CreatedBy = Jane,
        });
        await db.SaveChangesAsync(Cancel);
    }

    private async Task<int> AddGroupAsync(params (int RelayId, string Role)[] members)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true, MaxOnMinutes = RelayGroup.DefaultMaxOnMinutes };
        for (var i = 0; i < members.Length; i++)
        {
            group.AddStep(members[i].Role, members[i].RelayId, startDelaySec: 3);
        }

        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }
}
