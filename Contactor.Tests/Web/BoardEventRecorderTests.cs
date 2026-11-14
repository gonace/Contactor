using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        await rig.Control.TurnAllOffAsync(rig.Live.BoardId, Jane, Cancel);
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

    // A new address: the old board's relays say nothing about the new one, so its first status is taken as at start and
    // nothing is logged as switched outside the app for being different.
    [Fact]
    public async Task Should_CompareNoRelaysWithTheOldBoard_AfterANewAddress()
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 1);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 1, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        var recorder = database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await recorder.StartAsync(Cancel);
        try
        {
            board.SetRelayAndInput(relay: 1, input: 1, on: true);
            await BoardTestRig.WaitUntilAsync(() => Events(database).Count(e => e.RelayId == 1) == 1);

            // The address changes and the board there has relay 1 off.
            board.Forget();
            board.SetConnected(true);
            board.SetInput(1, on: false);
            await BoardTestRig.WaitUntilAsync(() => Events(database).Count(e => e.Action == EventActions.InputChanged) >= 2);

            Assert.Single(Events(database), e => e.RelayId == 1);
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    // A board with several inputs, e.g. the VM204: each change is logged with its input.
    [Fact]
    public async Task Should_LogEveryInput_WithItsNumber()
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 4);
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 4, Cancel);
        await database.Services.UseFakeBoardAsync(board);
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

    // A reboot or an upgrade is a short gap: the inputs' times stay. A long one (a crash, a board that was off) doesn't.
    [Theory]
    [InlineData(2, false)]
    [InlineData(60, true)]
    public async Task Should_MarkALongGap_AtStart(int minutesAway, bool longGap)
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-minutesAway));

        await setup.StartAsync();

        var state = setup.State();
        Assert.Equal(longGap ? BoardTestRig.Start : null, state.LongGapEndedUtc);
        Assert.Equal(BoardTestRig.Start, state.LastContactUtc);
    }

    // At start the inputs are compared with what the log last says: a change made while the app was down is logged, as
    // noticed; an input that reads the same logs nothing.
    [Fact]
    public async Task Should_LogAnInputThatChangedWhileDown_AsNoticed()
    {
        // The board's inputs as it reads them at its first answer after start.
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-2), inputsOn: [1, 2]);
        setup.Log(1, on: false);
        setup.Log(2, on: true);

        await setup.StartAsync();

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 3);
        var noticed = InputEvents(setup.Database)[2];
        Assert.Equal((1, """{"on":true,"noticed":true}"""), (noticed.InputId ?? 0, noticed.Details ?? ""));
    }

    // A change seen live is logged as it is; one made while contact was lost is logged at the reconnect, as noticed.
    [Fact]
    public async Task Should_MarkOnlyChangesMadeDuringALostContact_AsNoticed()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();

        setup.Board.SetInput(1, on: true);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 1);
        setup.Board.SetConnected(false);
        setup.Board.SetInput(1, on: false);
        setup.Board.SetConnected(true);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);

        Assert.Equal(["""{"on":true}""", """{"on":false,"noticed":true}"""], InputEvents(setup.Database).Select(e => e.Details));
    }

    // A lost contact longer than the limit, while the app runs, ends as a long gap; a short one doesn't.
    [Theory]
    [InlineData(2, false)]
    [InlineData(11, true)]
    public async Task Should_MarkALongLostContact(int minutesLost, bool longGap)
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        Assert.Null(setup.State().LongGapEndedUtc);

        setup.Board.SetConnected(false);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        setup.Time.Now += TimeSpan.FromMinutes(minutesLost);
        var back = setup.Time.Now.UtcDateTime;
        setup.Board.SetConnected(true);

        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == back);
        Assert.Equal(longGap ? back : null, setup.State().LongGapEndedUtc);
    }

    // A clock that went back (a Raspberry Pi after a power cut, before it reaches a time server) can't measure the gap.
    [Fact]
    public async Task Should_CountAClockThatWentBack_AsALongGap()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddHours(1));

        await setup.StartAsync();

        Assert.Equal(BoardTestRig.Start, setup.State().LongGapEndedUtc);
    }

    // The time of contact is saved every few minutes while there is contact, and not more often.
    [Fact]
    public async Task Should_SaveTheTimeOfContact_EveryFewMinutes()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();

        setup.Time.Now += BoardEventRecorder.ContactSaveInterval - TimeSpan.FromSeconds(1);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, Cancel);
        Assert.Equal(BoardTestRig.Start, setup.State().LastContactUtc);

        setup.Time.Now += TimeSpan.FromSeconds(1);
        var due = setup.Time.Now.UtcDateTime;
        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == due);
    }

    // A save that fails when contact comes back loses nothing: the next status records the change and the short gap.
    [Fact]
    public async Task Should_StartOver_WhenTheSaveAfterALostContactFails()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        setup.Board.SetConnected(false);
        setup.Board.SetInput(1, on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");

        setup.Board.SetConnected(true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.Count > 0);
        Assert.Empty(InputEvents(setup.Database));
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");
        setup.Board.SetInput(2, on: true);

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);
        Assert.Contains(InputEvents(setup.Database), e => e is { InputId: 1, Details: """{"on":true,"noticed":true}""" });
        Assert.Null(setup.State().LongGapEndedUtc);
    }

    // The app's own change, matched by a status whose save fails, stays the app's on the next try: it isn't logged as
    // external, and the relay keeps its runtime (e.g. its auto-off).
    [Fact]
    public async Task Should_ExpectTheAppsOwnChangeAgain_WhenTheSaveFails()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        setup.Database.Services.Live().Expected.Expect(ChannelMask.Of(1), on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");

        setup.Board.SetRelayAndInput(relay: 1, input: 1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.Count > 0);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");
        setup.Board.SetInput(2, on: true);

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);
        Assert.DoesNotContain(Events(setup.Database), e => e.RelayId == 1);
    }

    // A polled board reports a state only when it changes, so the recorder doesn't wait for the next one: the same
    // status is tried again by itself, waiting longer after each failure, and the app's own change stays the app's
    // even when its expectation would have run out between the tries.
    [Fact]
    public async Task Should_TryTheSameStatusAgain_WithoutWaitingForTheBoard()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        setup.Database.Services.Live().Expected.Expect(ChannelMask.Of(1), on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");

        setup.Board.SetRelayAndInput(relay: 1, input: 1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);
        setup.Time.Now += ExpectedChanges.Lifetime;
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 2);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");

        // The second failure waits twice as long: not yet after 1 s.
        setup.Time.Now += BoardEventRecorder.CheckInterval;
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, TestContext.Current.CancellationToken);
        Assert.Empty(InputEvents(setup.Database));

        // No new status from the board: the retry records it, once.
        setup.Time.Now += BoardEventRecorder.CheckInterval;
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 1);
        Assert.Contains(InputEvents(setup.Database), e => e is { InputId: 1, Details: """{"on":true}""" });
        Assert.DoesNotContain(Events(setup.Database), e => e.RelayId == 1);
        Assert.Equal(2, setup.Errors.RecorderFailures);
    }

    // A status from a board then forgotten (another board at a new address) isn't tried again: the next status is
    // taken as at start. A lost connection keeps it (Should_KeepTheRetry_OverALostConnection).
    [Fact]
    public async Task Should_DropTheRetry_WhenTheStateIsForgotten()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Board.SetRelayAndInput(relay: 1, input: 1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);

        setup.Board.Forget();

        await setup.ExecuteAsync("DROP TRIGGER fail_once;");
        setup.Time.Now += BoardEventRecorder.CheckInterval;
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, TestContext.Current.CancellationToken);

        // Nothing of the status that failed: its input and relay changes are compared afresh with the next one.
        Assert.DoesNotContain(Events(setup.Database), e => e.InputId is not null || e.RelayId is not null);
        Assert.Equal(1, setup.Errors.RecorderFailures);
    }

    // An input closes and opens again before the failed status is tried again: the waiting status is tried first, so
    // both changes are logged and both reach the input's actions.
    [Fact]
    public async Task Should_RecordAQuickChange_WhenANewerStatusComesBeforeTheRetry()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Board.SetInput(1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");

        setup.Board.SetInput(1, on: false);

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);
        Assert.Equal(["""{"on":true}""", """{"on":false}"""], InputEvents(setup.Database).Select(e => e.Details));
        var feed = setup.Database.Services.GetRequiredService<InputChangeFeed>().Reader;
        List<InputChanged> handedOn = [];
        while (feed.TryRead(out var change))
        {
            handedOn.Add(change);
        }

        Assert.Equal([new InputChanged(1, true), new InputChanged(1, false)], handedOn);
    }

    // A lost connection keeps the status waiting for its retry. It is recorded as of before the loss: the app's own
    // change, long expired by then, is still the app's, and its input's change is logged as seen but no longer acts.
    // What changed during the loss is noticed when contact comes back, and doesn't act either.
    [Fact]
    public async Task Should_KeepTheRetry_OverALostConnection()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        setup.Board.Time = setup.Time;
        await setup.StartAsync();
        setup.Database.Services.Live().Expected.Expect(ChannelMask.Of(1), on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Board.SetRelayAndInput(relay: 1, input: 1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);

        // The lost connection tries the waiting status first, which fails again.
        setup.Time.Now += TimeSpan.FromSeconds(10);
        setup.Board.SetConnected(false);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 2);
        setup.Board.SetInput(2, on: true);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");
        setup.Time.Now += TimeSpan.FromMinutes(20);

        setup.Board.SetConnected(true);

        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);
        Assert.Equal(
            [(1, """{"on":true}"""), (2, """{"on":true,"noticed":true}""")],
            InputEvents(setup.Database).Select(e => (e.InputId!.Value, e.Details)));
        Assert.DoesNotContain(Events(setup.Database), e => e.RelayId == 1);
        Assert.False(setup.Database.Services.GetRequiredService<InputChangeFeed>().Reader.TryRead(out _));
    }

    // A status from before a lost connection, recorded while there is still no contact, doesn't save a time of contact:
    // a restart during the loss would read the gap as shorter than it was.
    [Fact]
    public async Task Should_NotSaveATimeOfContact_ForAStatusRecordedDuringALoss()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        setup.Board.Time = setup.Time;
        await setup.StartAsync();
        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == BoardTestRig.Start);

        // The status that ends a short gap fails, then contact is lost again before it is recorded.
        setup.Board.SetConnected(false);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Time.Now += TimeSpan.FromSeconds(10);
        setup.Board.SetInput(1, on: true);
        setup.Board.SetConnected(true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);
        setup.Board.SetConnected(false);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 2);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");

        // The retry, while there is still no contact.
        setup.Time.Now += TimeSpan.FromMinutes(20);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 1);

        Assert.Equal(BoardTestRig.Start, setup.State().LastContactUtc);

        // The gap it ended was short: it is measured to when the status came, not to the retry 20 minutes later.
        Assert.Null(setup.State().LongGapEndedUtc);
    }

    // A status from before a lost connection that ended a long gap, recorded while there is still no contact: the gap
    // ended when that status came, not when it is recorded, so the inputs' times are trusted from then.
    [Fact]
    public async Task Should_EndALongGap_WhenTheStatusCame_ForAStatusRecordedDuringALoss()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        setup.Board.Time = setup.Time;
        await setup.StartAsync();
        await BoardTestRig.WaitUntilAsync(() => setup.State().LastContactUtc == BoardTestRig.Start);

        // A long gap; the status that ends it fails, then contact is lost again before it is recorded.
        setup.Board.SetConnected(false);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Time.Now += TimeSpan.FromMinutes(20);
        var came = setup.Time.Now.UtcDateTime;
        setup.Board.SetInput(1, on: true);
        setup.Board.SetConnected(true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);
        setup.Board.SetConnected(false);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 2);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");

        // The retry, five minutes later, while there is still no contact.
        setup.Time.Now += TimeSpan.FromMinutes(5);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 1);

        Assert.Equal(came, setup.State().LongGapEndedUtc);
        Assert.Equal(BoardTestRig.Start, setup.State().LastContactUtc);
    }

    // A clock that went back (a Raspberry Pi after a power cut): the saved long gap's end is ahead of it. A status from
    // before a lost connection, recorded during the loss, ends the gap when it came, and doesn't keep that future end,
    // which would read every input change until then as "since unknown".
    [Fact]
    public async Task Should_NotKeepAFutureGapEnd_ForAStatusRecordedDuringALoss()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddHours(1), longGapEnded: BoardTestRig.Start.AddHours(1));
        setup.Board.Time = setup.Time;
        setup.Board.SetConnected(true);
        setup.Log(1, on: false);
        setup.Board.SetInput(1, on: true);
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");

        // The first status fails, then contact is lost before it is recorded.
        await setup.StartAsync(waitForContact: false);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);
        setup.Board.SetConnected(false);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 2);
        await setup.ExecuteAsync("DROP TRIGGER fail_once;");

        // The retry, a minute later, while there is still no contact.
        setup.Time.Now += TimeSpan.FromMinutes(1);
        await BoardTestRig.WaitUntilAsync(() => InputEvents(setup.Database).Count == 2);

        Assert.Equal(BoardTestRig.Start, setup.State().LongGapEndedUtc);
    }

    // While the database stays broken, a newer status that replaces a waiting one keeps the grown wait between tries.
    [Fact]
    public async Task Should_KeepTheGrownWait_WhenANewerStatusReplacesAFailedOne()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();
        await setup.ExecuteAsync("CREATE TRIGGER fail_once BEFORE INSERT ON \"EventLog\" BEGIN SELECT RAISE(ABORT, 'disk busy'); END;");
        setup.Board.SetInput(1, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 1);

        // The waiting status (2 s next), then the newer one (4 s next).
        setup.Board.SetInput(2, on: true);
        await BoardTestRig.WaitUntilAsync(() => setup.Errors.RecorderFailures == 3);
        setup.Time.Now += TimeSpan.FromSeconds(3);
        await Task.Delay(BoardEventRecorder.CheckInterval * 2, TestContext.Current.CancellationToken);

        Assert.Equal(3, setup.Errors.RecorderFailures);
    }

    // A relay that comes on outside the app gets its own auto-off, so nothing runs for ever by accident; one that goes
    // off loses its run.
    [Fact]
    public async Task Should_GiveARelayOnOutsideTheApp_ItsOwnAutoOff()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await setup.StartAsync();

        setup.Board.SetRelayAndInput(relay: 3, input: 1, on: true);
        await BoardTestRig.WaitUntilAsync(() => Runtime(setup.Database, 3).TurnedOnBy == EventActors.External);

        var runtime = Runtime(setup.Database, 3);
        Assert.Equal((BoardTestRig.Start, BoardTestRig.Start.AddMinutes(Relay.DefaultMaxOnMinutes)), (runtime.OnSinceUtc, runtime.AutoOffAtUtc));
        var entry = Assert.Single(Events(setup.Database), e => e.RelayId == 3);
        Assert.Equal((EventActors.External, EventActions.On), (entry.Actor, entry.Action));

        setup.Board.SetRelayAndInput(relay: 3, input: 1, on: false);
        await BoardTestRig.WaitUntilAsync(() => Runtime(setup.Database, 3).TurnedOnBy is null);
        Assert.Null(Runtime(setup.Database, 3).AutoOffAtUtc);
    }

    // At start, a relay that is on with nobody's run gets its own auto-off from then; one a person turned on keeps theirs.
    [Fact]
    public async Task Should_GiveAnOwnerlessRelay_ItsOwnAutoOff_AtStart()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1), relaysOn: [2, 3]);
        await using (var db = await setup.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var person = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
            (person.OnSinceUtc, person.AutoOffAtUtc, person.TurnedOnBy) = (BoardTestRig.Start.AddMinutes(-30), BoardTestRig.Start.AddMinutes(90), "jane@example.com");
            await db.SaveChangesAsync(Cancel);
        }

        await setup.StartAsync();
        await BoardTestRig.WaitUntilAsync(() => Runtime(setup.Database, 3).TurnedOnBy == EventActors.External);

        Assert.Equal(BoardTestRig.Start.AddMinutes(Relay.DefaultMaxOnMinutes), Runtime(setup.Database, 3).AutoOffAtUtc);
        Assert.Equal(("jane@example.com", (DateTime?)BoardTestRig.Start.AddMinutes(90)), (Runtime(setup.Database, 2).TurnedOnBy, Runtime(setup.Database, 2).AutoOffAtUtc));
    }

    // A relay in a group gets no auto-off of its own (its group's run applies), and one set to "Never" none at all,
    // though it shows as on outside the app.
    [Fact]
    public async Task Should_GiveNoOwnAutoOff_ToAGroupsRelay_OrOneSetToNever()
    {
        await using var setup = await InputRigAsync(lastContact: BoardTestRig.Start.AddMinutes(-1));
        await using (var db = await setup.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan" });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
            db.RelayGroups.Add(group);
            (await db.Relays.SingleAsync(r => r.Id == 4, Cancel)).MaxOnMinutes = null;
            await db.SaveChangesAsync(Cancel);
        }

        await setup.StartAsync();
        setup.Board.SetRelayAndInput(relay: 1, input: 1, on: true);
        setup.Board.SetRelayAndInput(relay: 4, input: 2, on: true);
        await BoardTestRig.WaitUntilAsync(() => Runtime(setup.Database, 4).TurnedOnBy == EventActors.External);

        Assert.Null(Runtime(setup.Database, 4).AutoOffAtUtc);
        Assert.Null(Runtime(setup.Database, 1).TurnedOnBy);
    }

    private static RelayRuntime Runtime(TestDatabase database, int relayId)
    {
        using var db = database.Factory.CreateDbContext();
        return db.RelayRuntimes.AsNoTracking().Single(r => r.RelayId == relayId);
    }

    private static List<EventLogEntry> InputEvents(TestDatabase database)
    {
        return [.. Events(database).Where(e => e.Action == EventActions.InputChanged)];
    }

    private static async Task<InputRig> InputRigAsync(DateTime lastContact, int[]? relaysOn = null, DateTime? longGapEnded = null, int[]? inputsOn = null)
    {
        var board = new FakeRelayBoard(relayCount: 4, inputCount: 2);
        foreach (var relay in relaysOn ?? [])
        {
            await board.TurnOnAsync(ChannelMask.Of(relay), Cancel);
        }

        foreach (var input in inputsOn ?? [])
        {
            board.SetInput(input, on: true);
        }

        var time = new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start));
        var errors = new ErrorCounter();
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<ILoggerProvider>(errors);
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(time);
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSingleton<InputChangeFeed>();
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: 2, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
            (runtime.LastContactUtc, runtime.LongGapEndedUtc) = (lastContact, longGapEnded);
            await db.SaveChangesAsync(Cancel);
        }

        return new InputRig(board, database, time, errors);
    }

    /// <summary>Counts the errors logged, so a test can wait for a failure the code under test caught.</summary>
    private sealed class ErrorCounter : ILoggerProvider, ILogger
    {
        private int _count;
        private int _recorderFailures;

        public int Count => Volatile.Read(ref _count);

        /// <summary>The recorder's own "Could not record" errors only: one per failed try, whatever else logs it.</summary>
        public int RecorderFailures => Volatile.Read(ref _recorderFailures);

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Error;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Interlocked.Increment(ref _count);
                if (formatter(state, exception).StartsWith("Could not record an event of relay board", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _recorderFailures);
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class InputRig : IAsyncDisposable
    {
        private BoardEventRecorder? _recorder;

        public InputRig(FakeRelayBoard board, TestDatabase database, FixedTimeProvider time, ErrorCounter errors)
        {
            Board = board;
            Database = database;
            Time = time;
            Errors = errors;
        }

        /// <summary>How many errors were logged, e.g. a failed save the recorder caught.</summary>
        public ErrorCounter Errors { get; }

        public FixedTimeProvider Time { get; }

        public FakeRelayBoard Board { get; }

        public TestDatabase Database { get; }

        // Starts the recorder, and waits for its first status to save the time of contact, unless that is to fail.
        public async Task StartAsync(bool waitForContact = true)
        {
            _recorder = Database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
            await _recorder.StartAsync(Cancel);
            if (waitForContact)
            {
                await BoardTestRig.WaitUntilAsync(() => State().LastContactUtc == BoardTestRig.Start);
            }
        }

        public void Log(int input, bool on)
        {
            using var db = Database.Factory.CreateDbContext();
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = BoardTestRig.Start.AddHours(-1),
                InputId = input,
                Actor = EventActors.External,
                Action = EventActions.InputChanged,
                Details = on ? """{"on":true}""" : """{"on":false}""",
            });
            db.SaveChanges();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            await db.Database.ExecuteSqlRawAsync(sql, Cancel);
        }

        public BoardRuntime State()
        {
            using var db = Database.Factory.CreateDbContext();
            return db.BoardRuntimes.AsNoTracking().Single();
        }

        public async ValueTask DisposeAsync()
        {
            if (_recorder is not null)
            {
                await _recorder.StopAsync(CancellationToken.None);
            }

            await Database.DisposeAsync();
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
