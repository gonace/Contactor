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
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// A command the board carried out but whose answer was lost or unreadable: the app logged nothing, so the change the
/// next status shows is logged as external, not taken for the app's own and lost.
/// </summary>
public sealed class FailedCommandTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private const string John = "john@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    private readonly FakeRelayBoard _board = new(4);
    private readonly FixedTimeProvider _time = new(Now);
    private TestDatabase _database = null!;
    private BoardEventRecorder _recorder = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    private int BoardId => _database.Services.Live().BoardId;

    private AutoOffService AutoOff => _database.Services.GetRequiredService<AutoOffService>();

    private SchedulerService Scheduler => _database.Services.GetRequiredService<SchedulerService>();

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
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnAllOffAsync(BoardId, Jane, Cancel));
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
        await Control.TurnAllOffAsync(BoardId, Jane, Cancel);
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

    // A relay whose answer the board couldn't confirm, but which the board's state after the command shows off, went off
    // all the same: "All off" logs it and ends its run, whichever comes first, the command's return or the recorder's
    // turn with that state.
    [Fact]
    public async Task Should_LogARelayThatWentOffUnconfirmed_InAllOff()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(2);

        _board.Unconfirmed = ChannelMask.Of(3);
        await Control.TurnAllOffAsync(BoardId, Jane, Cancel);
        await _board.RefreshAsync(Cancel);

        var events = await WaitForEventsAsync(4);
        Assert.Equal([(2, EventActions.On), (3, EventActions.On), (2, EventActions.Off), (3, EventActions.Off)], events.Select(e => (e.RelayId!.Value, e.Action)));
        Assert.All(events, e => Assert.Equal(Jane, e.Actor));
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.Null((await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel)).OnSinceUtc);
        }

        await AssertNoExternalEventAsync();
    }

    // A group's ON the board refused, sent together with another group's: when that relay comes on later, it is logged
    // as changed outside the app, not taken for the step the board refused.
    [Fact]
    public async Task Should_LogARelayRefusedInAGroupsOn_AsExternal_WhenItComesOnLater()
    {
        var first = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        var second = await AddGroupAsync((3, "Fan"), (4, "Heat"));
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();
        await sequencer.StartGroupAsync(first, Jane, Cancel);
        Assert.True(_board.State.IsOn(1));
        await sequencer.StepAsync(Cancel);

        // The first group's heat is due when the second group starts: both ONs go in one command.
        _time.Now += TimeSpan.FromSeconds(3);
        _board.Refuse = ChannelMask.Of(2);
        await sequencer.StartGroupAsync(second, Jane, Cancel);
        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(3)));
        _board.Refuse = ChannelMask.None;
        _board.SetRelay(2, on: true);

        var events = await WaitForEventsAsync(e => e.Any(x => x is { RelayId: 2, Actor: EventActors.External, Action: EventActions.On }));
        Assert.Contains(events, e => e is { RelayId: 2, Actor: EventActors.External, Action: EventActions.On });
    }

    // A member's ON the board refused, sent together with another group's step: nothing is logged for it, its group's
    // run stays Jane's as it was, the other group's step goes on, and John is told the board didn't do it.
    [Fact]
    public async Task Should_KeepTheGroupsRun_WhenTheBoardRefusesAMembersOn_SentWithAnotherGroupsStep()
    {
        var (first, second) = await StartFanAndSecondGroupAsync(fanBy: Jane);
        var before = await RuntimeAsync(first);

        _board.Refuse = ChannelMask.Of(2);
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();
        var ex = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sequencer.SetMemberAsync(first, 2, on: true, John, Cancel));

        Assert.Equal("The board didn't take the command.", ex.Message);
        Assert.Equal((ChannelMask.Of([2, 4]), true), _board.Sent[^1]);
        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(4)));
        Assert.Equal(before, await RuntimeAsync(first));
        var events = await GroupEventsAsync();
        Assert.DoesNotContain(events, e => e.RelayId == 2);
        Assert.Contains(events, e => e.GroupId == second && e.Action == EventActions.GroupOn);
    }

    // A member's ON that is the pass's only ON, sent after another group's OFF: the board refuses it, so the ON fails
    // as a whole. The OFF went out and is saved; the member's ON leaves nothing behind.
    [Fact]
    public async Task Should_KeepTheGroupsRun_WhenTheBoardRefusesAMembersOn_SentAfterAnotherGroupsOff()
    {
        var (first, second) = await StartFanAndSecondGroupAsync(fanBy: Jane);
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();
        _time.Now += TimeSpan.FromSeconds(3);
        await sequencer.StepAsync(Cancel);
        Assert.True(_board.State.IsOn(4));
        await sequencer.StepAsync(Cancel);

        // Heat off first; the fan, with no run-on, goes off in the next pass, with John's ON.
        await sequencer.StopGroupAsync(second, Jane, Cancel);
        Assert.False(_board.State.IsOn(4));
        var before = await RuntimeAsync(first);

        _board.Refuse = ChannelMask.Of(2);
        var ex = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sequencer.SetMemberAsync(first, 2, on: true, John, Cancel));

        Assert.Equal("The board refused the command.", ex.Message);
        Assert.Equal([(ChannelMask.Of(3), false), (ChannelMask.Of(2), true)], _board.Sent.TakeLast(2));
        Assert.Equal((false, false), (_board.State.IsOn(2), _board.State.IsOn(3)));
        Assert.Equal(before, await RuntimeAsync(first));
        var events = await GroupEventsAsync();
        Assert.DoesNotContain(events, e => e.RelayId == 2);

        // The second group's OFF is saved: its stop ends at the next pass, which sends nothing for the first group.
        var stopping = await RuntimeAsync(second);
        Assert.Equal((1, false), (stopping.PendingStep, stopping.PendingOn));
        var sent = _board.Sent.Count;
        await sequencer.StepAsync(Cancel);
        Assert.Equal(GroupPhases.Idle, (await RuntimeAsync(second)).Phase);
        Assert.Equal(sent, _board.Sent.Count);
        Assert.Equal(before, await RuntimeAsync(first));
    }

    // The same command with nothing refused: the member's ON is logged once, and the run is the person's.
    [Fact]
    public async Task Should_LogAMembersOn_WhenSentWithAnotherGroupsStep()
    {
        var (first, _) = await StartFanAndSecondGroupAsync(fanBy: John);

        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();
        Assert.Null(await sequencer.SetMemberAsync(first, 2, on: true, Jane, Cancel));

        Assert.Equal((ChannelMask.Of([2, 4]), true), _board.Sent[^1]);
        Assert.Equal((true, true), (_board.State.IsOn(2), _board.State.IsOn(4)));
        var on = Assert.Single(await GroupEventsAsync(), e => e.RelayId == 2);
        Assert.Equal((Jane, EventActions.On, first), (on.Actor, on.Action, on.GroupId!.Value));
        Assert.Equal(Jane, (await RuntimeAsync(first)).TurnedOnBy);
    }

    // A regression guard: a relay of its own the board refuses throws, logs nothing and starts no run.
    [Fact]
    public async Task Should_LogNothing_WhenTheBoardRefusesARelaysOn()
    {
        _board.Refuse = ChannelMask.Of(3);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => Control.TurnOnAsync(3, Jane, Cancel));

        Assert.False(_board.State.IsOn(3));
        await AssertNoExternalEventAsync();
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        Assert.DoesNotContain(await db.EventLog.ToListAsync(Cancel), e => e.RelayId == 3);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
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
        await sequencer.StopAllAsync(BoardId, Jane, Cancel);
        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(4)));
        _board.Refuse = ChannelMask.None;
        _board.SetRelay(4, on: false);

        var events = await WaitForEventsAsync(e => e.Any(x => x is { RelayId: 4, Action: EventActions.Off }));
        Assert.Equal(EventActors.External, events.Single(e => e is { RelayId: 4, Action: EventActions.Off }).Actor);
    }

    // A relay whose answer the board couldn't confirm in a group's stop, but which the board's state after the command
    // shows off, went off all the same: it is the stop's, not changed outside the app, whichever comes first, the
    // command's return or the recorder's turn with that state.
    [Fact]
    public async Task Should_LogARelayThatWentOffUnconfirmed_InAGroupStop()
    {
        var first = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        var second = await AddGroupAsync((3, "Fan"), (4, "Heat"));
        foreach (var relay in new[] { 1, 2, 3, 4 })
        {
            _board.SetRelay(relay, on: true);
        }

        await WaitForEventsAsync(4);
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();

        _board.Unconfirmed = ChannelMask.Of(4);
        await sequencer.StopAllAsync(BoardId, Jane, Cancel);
        Assert.Equal((false, false), (_board.State.IsOn(2), _board.State.IsOn(4)));
        await _board.RefreshAsync(Cancel);

        // The recorder handles each status in order: once the input's is logged, an OFF from before would be too.
        _board.SetInput(1, true);
        var events = await WaitForEventsAsync(e => e.Any(x => x.Action == EventActions.InputChanged));
        Assert.Equal([first, second], events.Where(e => e.Action == EventActions.GroupOff).Select(e => e.GroupId!.Value).Order());
        Assert.DoesNotContain(events, e => e.Action == EventActions.Off);
    }

    // An auto-off the board refuses changes nothing: it isn't logged, the run stays, and the next check tries again.
    // A regression guard: auto-off switches one relay at a time, so this was right already before the app logged only
    // the relays a command really switched.
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

    // A schedule's ON cancelled before it reached the board leaves no expectation: the relay coming on at the board
    // right after is logged as changed there, not taken for the schedule's.
    [Fact]
    public async Task Should_LogExternal_WhenAScheduleOnWasCancelledBeforeItWentOut()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        using var caller = new CancellationTokenSource();
        _board.BeforeOn = () => CancelBeforeItGoesOut(caller);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.TickAsync(caller.Token));

        await AssertLoggedAsExternalAsync(2, on: true);
    }

    // A schedule's OFF that fails other than with a lost board leaves no expectation either.
    [Fact]
    public async Task Should_LogExternal_WhenAScheduleOffFailedBeforeItWentOut()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        await WaitForEventsAsync(1);
        _time.Now = OffTime;
        _board.BeforeOff = FailBeforeItGoesOut;

        await Assert.ThrowsAsync<TimeoutException>(() => Scheduler.TickAsync(Cancel));

        await AssertLoggedAsExternalAsync(2, on: false);
    }

    [Fact]
    public async Task Should_LogExternal_WhenAnAutoOffWasCancelledBeforeItWentOut()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        using var caller = new CancellationTokenSource();
        _board.BeforeOff = () => CancelBeforeItGoesOut(caller);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoOff.CheckAsync(caller.Token));

        await AssertLoggedAsExternalAsync(3, on: false);
    }

    [Fact]
    public async Task Should_LogExternal_WhenAnAutoOffFailedBeforeItWentOut()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        await WaitForEventsAsync(1);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        _board.BeforeOff = FailBeforeItGoesOut;

        await Assert.ThrowsAsync<TimeoutException>(() => AutoOff.CheckAsync(Cancel));

        await AssertLoggedAsExternalAsync(3, on: false);
    }

    // The caller cancels while the command waits to go out, as a board's client does before it sends: nothing reaches
    // the board.
    private Task CancelBeforeItGoesOut(CancellationTokenSource caller)
    {
        _board.BeforeOn = null;
        _board.BeforeOff = null;
        caller.Cancel();
        throw new OperationCanceledException(caller.Token);
    }

    // A failure other than a lost board, before the command reaches the board.
    private Task FailBeforeItGoesOut()
    {
        _board.BeforeOff = null;
        throw new TimeoutException("No answer.");
    }

    // Nothing reached the board and no change is expected: the relay switching at the board right after is logged as
    // changed there.
    private async Task AssertLoggedAsExternalAsync(int relayId, bool on)
    {
        Assert.Equal(!on, _board.State.IsOn(relayId));
        Assert.False(_database.Services.Live().Expected.IsPending(relayId, on));
        _board.SetRelay(relayId, on);
        var action = on ? EventActions.On : EventActions.Off;
        var events = await WaitForEventsAsync(e => e.Any(x => x.RelayId == relayId && x.Action == action));
        Assert.Equal(EventActors.External, events.Single(e => e.RelayId == relayId && e.Action == action).Actor);
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
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true };
        for (var i = 0; i < members.Length; i++)
        {
            group.AddStep(members[i].Role, members[i].RelayId, startDelaySec: 3);
        }

        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }

    // The first group (1 Fan, 2 Heat) with its fan on, turned on by fanBy; the second (3 Fan, 4 Heat) started by Jane
    // and its heat due, so the next pass sends relay 4's ON.
    private async Task<(int First, int Second)> StartFanAndSecondGroupAsync(string fanBy)
    {
        var first = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        var second = await AddGroupAsync((3, "Fan"), (4, "Heat"));
        var sequencer = _database.Services.GetRequiredService<GroupSequencer>();
        Assert.Null(await sequencer.SetMemberAsync(first, 1, on: true, fanBy, Cancel));
        await sequencer.StepAsync(Cancel);
        await sequencer.StartGroupAsync(second, Jane, Cancel);
        await sequencer.StepAsync(Cancel);
        Assert.Equal((true, true), (_board.State.IsOn(1), _board.State.IsOn(3)));
        _time.Now += TimeSpan.FromSeconds(3);
        return (first, second);
    }

    private async Task<(string Phase, int TargetLevel, DateTime? NextStepAtUtc, int? PendingStep, int? PendingRelayId, bool PendingOn, DateTime? PendingSinceUtc, string StopRelayIds, DateTime? OnSinceUtc, DateTime? AutoOffAtUtc, string? TurnedOnBy)> RuntimeAsync(int groupId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var r = await db.GroupRuntimes.SingleAsync(x => x.GroupId == groupId, Cancel);
        return (r.Phase, r.TargetLevel, r.NextStepAtUtc, r.PendingStep, r.PendingRelayId, r.PendingOn, r.PendingSinceUtc, string.Join(',', r.StopRelayIds), r.OnSinceUtc, r.AutoOffAtUtc, r.TurnedOnBy);
    }

    // The events the sequencer saved; it saves them before its call returns.
    private async Task<List<EventLogEntry>> GroupEventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.Where(e => e.GroupId != null).OrderBy(e => e.Id).ToListAsync(Cancel);
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
