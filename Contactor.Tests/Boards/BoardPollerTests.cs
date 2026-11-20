using Contactor.Boards;
using Contactor.Tests.Web;

namespace Contactor.Tests.Boards;

/// <summary>The protocol-neutral polling loop, with a fake board and a clock the test moves: no real waits.</summary>
public sealed class BoardPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheFirstAnswer_ThenOnlyChanges()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, 1, 2);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await WaitForPollAsync(board, time, reads: 1);
        Assert.Equal([new PolledState<int>(true, 1, time.GetUtcNow())], board.Changes);

        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);
        Assert.Single(board.Changes);
        Assert.Equal(time.GetUtcNow(), poller.State.LastContactUtc);

        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 3);
        Assert.Equal(2, board.Changes.Count);
        Assert.Equal(2, board.Changes[^1].Reading);

        await stop.CancelAsync();
        await run;
        Assert.False(poller.State.IsConnected);
        Assert.False(board.Changes[^1].IsConnected);
        Assert.Equal(2, board.Changes[^1].Reading);
        Assert.Contains((BoardPollStep.Stopped, null), board.Steps);
    }

    [Fact]
    public async Task Should_UseTheComparer_ToDecideWhatIsAChange()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(10, 11);
        var tens = EqualityComparer<int>.Create((a, b) => a / 10 == b / 10, v => v / 10);
        var poller = new BoardPoller<int>(board, time, tens) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);

        Assert.Single(board.Changes);
        Assert.Equal(11, poller.State.Reading);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_TellAChangeTheComparerLeavesOut_AsAReadingChange_NeverAsAStateChange()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(10, 11, 11, 25);
        var tens = EqualityComparer<int>.Create((a, b) => a / 10 == b / 10, v => v / 10);
        var poller = new BoardPoller<int>(board, time, tens) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        Assert.Empty(board.ReadingChanges);

        // Only what the comparer leaves out changed: a reading change, never a state change.
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);
        Assert.Single(board.Changes);
        Assert.Equal([new PolledState<int>(true, 11, time.GetUtcNow())], board.ReadingChanges);

        // Nothing changed: neither.
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 3);
        Assert.Single(board.Changes);
        Assert.Single(board.ReadingChanges);

        // A change the comparer sees: a state change only.
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 4);
        Assert.Equal(2, board.Changes.Count);
        Assert.Single(board.ReadingChanges);

        await stop.CancelAsync();
        await run;
        Assert.Single(board.ReadingChanges);
    }

    [Fact]
    public async Task Should_DropAReadingChange_FromAnAnswerBeforeForget()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(10);
        var tens = EqualityComparer<int>.Create((a, b) => a / 10 == b / 10, v => v / 10);
        var poller = new BoardPoller<int>(board, time, tens) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;
        poller.Forget();

        answer.SetResult(11);
        await command;
        Assert.Empty(board.ReadingChanges);
        Assert.DoesNotContain(board.Changes, c => c.Reading == 11);
        Assert.Equal(new PolledState<int>(false, 0, null), poller.State);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_StayConnectedThroughAFailedPoll_AndLoseTheConnectionAfterThree()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, new IOException("one"), 1, new IOException("a"), new IOException("b"), new IOException("c"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        for (var reads = 2; reads <= 5; reads++)
        {
            time.Advance(Interval);
            await WaitForPollAsync(board, time, reads);
            Assert.True(poller.State.IsConnected);
        }

        time.Advance(Interval);
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")));

        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "c"), board.Steps);
        Assert.DoesNotContain((BoardPollStep.Lost, "one"), board.Steps);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ConnectAgainWithAWaitGrowingTo30Seconds()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { FailConnect = true };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        int[] waits = [1, 2, 4, 8, 16, 30, 30];
        foreach (var wait in waits)
        {
            await WaitUntilAsync(() => board.Steps.Count(s => s.Kind == BoardPollStep.Waiting) == board.Connects && time.WaitsOnly(TimeSpan.FromSeconds(wait)));
            Assert.Equal((BoardPollStep.Waiting, wait.ToString(System.Globalization.CultureInfo.InvariantCulture)), board.Steps[^1]);
            board.FailConnect = board.Connects < waits.Length;
            time.Advance(TimeSpan.FromSeconds(wait));
        }

        await WaitForPollAsync(board, time, reads: 1);
        Assert.True(poller.State.IsConnected);
        Assert.True(board.Changes[^1].IsConnected);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_PollAtOnce_WhenAsked()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, 2);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        poller.PollNow();
        await WaitForPollAsync(board, time, reads: 2);

        Assert.Equal(2, poller.State.Reading);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ConnectAgainAtOnce_WhenRestarted()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        poller.Restart();
        await WaitForPollAsync(board, time, reads: 2);

        Assert.Equal(2, board.Connects);
        Assert.Contains((BoardPollStep.Stopped, null), board.Steps);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Waiting);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_RefuseToRunTwice()
    {
        var poller = new BoardPoller<int>(new FakePolledBoard(1), new ManualTimeProvider());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.RunAsync(stop.Token));

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public void Should_RefuseAnIntervalOrFailureCountThatCantWork()
    {
        var board = new FakePolledBoard(1);
        var time = new ManualTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new BoardPoller<int>(board, time) { PollInterval = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoardPoller<int>(board, time) { FailuresBeforeLost = 0 });
    }

    [Fact]
    public async Task Should_GiveUpAPollUnderWay_ForACommand_WithoutCountingItAsFailed()
    {
        var time = new ManualTimeProvider();
        var hanging = new HangingRead();
        var board = new FakePolledBoard(1, hanging, 1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval, FailuresBeforeLost = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        time.Advance(Interval);
        await WaitUntilAsync(() => hanging.Started);

        // The command's own answer is the confirmation: no second request.
        await poller.RunCommandAsync(_ => Task.FromResult(5), Cancel);

        Assert.True(hanging.IsCancelled);
        Assert.Equal(new PolledState<int>(true, 5, time.GetUtcNow()), poller.State);
        Assert.Equal(5, board.Changes[^1].Reading);
        await WaitUntilAsync(() => time.WaitsOnly(Interval));
        Assert.Equal(2, board.Reads);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);

        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 3);
        Assert.True(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    // Issue #379: the switches went out, but the read after them failed. That is no contact with the board: the last
    // reading and its time stay, the failed polls in a row aren't started over, and the board is read at once.
    [Fact]
    public async Task Should_NotCountACommand_AsAnAnsweredPoll_WhenItsReadFailed()
    {
        var time = new ManualTimeProvider();
        // The read at once waits until the test ends it. Else it can fail and lose the board before the asserts on the
        // state the command left run (Issue #436).
        var readAtOnce = new HangingRead();
        var board = new FakePolledBoard(1, new IOException("a"), readAtOnce);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval, FailuresBeforeLost = 2 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        var contact = time.GetUtcNow();
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);
        var changes = board.Changes.Count;

        await poller.RunSwitchesAsync(_ => Task.FromResult(default(CommandReading<int>)), Cancel);

        Assert.Equal(new PolledState<int>(true, 1, contact), poller.State);
        Assert.Equal(changes, board.Changes.Count);

        // The board is read at once, with no wait on the clock, and that read fails too: two in a row lose the board.
        await WaitUntilAsync(() => readAtOnce.Started);
        readAtOnce.Fail(new IOException("b"));
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Lost, "b")));
        Assert.Equal(3, board.Reads);
        Assert.False(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_TakeACommandsReading_AsAnAnsweredPoll()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, new IOException("a"), new IOException("b"), new IOException("c"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval, FailuresBeforeLost = 2 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);

        await poller.RunSwitchesAsync(_ => Task.FromResult(new CommandReading<int>(5)), Cancel);

        Assert.Equal(new PolledState<int>(true, 5, time.GetUtcNow()), poller.State);

        // The count started over: one more failed poll doesn't lose the board.
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 3);
        Assert.True(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_RefuseACommand_WhenThePollUnderWayLosesTheBoard()
    {
        var time = new ManualTimeProvider();
        var hanging = new HangingRead(honourCancel: false);
        var board = new FakePolledBoard(1, hanging);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval, FailuresBeforeLost = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        time.Advance(Interval);
        await WaitUntilAsync(() => hanging.Started);
        var sent = false;

        var command = poller.RunCommandAsync(
            _ =>
            {
                sent = true;
                return Task.FromResult(5);
            },
            Cancel);
        hanging.Fail(new IOException("gone"));

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => command);
        Assert.False(sent);
        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "gone"), board.Steps);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_RefuseACommand_WhileDisconnected_AndLeaveTheStateAlone_WhenItFails()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { FailConnect = true };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(_ => Task.FromResult(5), Cancel));

        board.FailConnect = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        await Assert.ThrowsAsync<IOException>(() => poller.RunCommandAsync(_ => Task.FromException<int>(new IOException("no")), Cancel));
        Assert.Equal(1, poller.State.Reading);
        Assert.True(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ConnectAgainAtOnce_WhenRestartedDuringTheWait()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { FailConnect = true };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")) && time.WaitsOnly(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "2")) && time.WaitsOnly(TimeSpan.FromSeconds(2)));

        board.FailConnect = false;
        poller.Restart();

        await WaitForPollAsync(board, time, reads: 1);
        Assert.Equal(3, board.Connects);
        Assert.True(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_SayStopped_WhenRestartEndsAConnectThatThenFails()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { HangConnect = true };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.Connects == 1);

        board.HangConnect = false;
        poller.Restart();

        await WaitForPollAsync(board, time, reads: 1);
        Assert.Contains((BoardPollStep.Stopped, null), board.Steps);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_NotPollTwice_ForAPollNowWhileDisconnected()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1).On(time);
        board.FailConnect = true;
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")) && time.WaitsOnly(TimeSpan.FromSeconds(1)));

        poller.PollNow();
        board.FailConnect = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForPollAsync(board, time, reads: 1);
        time.Advance(Interval);
        await WaitUntilAsync(() => board.Reads >= 2 && time.WaitsOnly(Interval));

        // The second poll came when it was due, not straight after the first.
        var reads = board.ReadTimes;
        Assert.Equal(Interval, reads[1] - reads[0]);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_LoseADeadBoard_AfterThreeUnansweredRequestsInARow_CountingEachOnce()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, new TimeoutException("poll"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        static Task<int> NoAnswer(CancellationToken token) => Task.FromException<int>(new TimeoutException("command"));

        // A command without an answer (1) is followed by a read at once, which fails too (2): still connected, so
        // the command was counted once, not twice.
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(NoAnswer, Cancel));
        await WaitForPollAsync(board, time, reads: 2);
        Assert.True(poller.State.IsConnected);
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(NoAnswer, Cancel));

        // The third in a row loses the board, and the poller connects again without reading the dead board first.
        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "command"), board.Steps);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(NoAnswer, Cancel));
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")));
        Assert.Equal(2, board.Reads);
        await stop.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData("no answer")]
    [InlineData("failed")]
    [InlineData("refused")]
    public async Task Should_ReadTheBoardAtOnce_AfterACommandThatFailed(string how)
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, 7);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        Exception failure = how switch
        {
            "no answer" => new TimeoutException(how),
            "refused" => new BoardRefusedException(how),
            _ => new InvalidOperationException(how),
        };

        await Assert.ThrowsAsync(failure.GetType(), () => poller.RunCommandAsync(_ => Task.FromException<int>(failure), Cancel));

        // No time passes: the read comes because the command failed, and shows what the board did.
        await WaitForPollAsync(board, time, reads: 2);
        Assert.Equal(7, poller.State.Reading);
        Assert.True(poller.State.IsConnected);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_CountAFailedCommand_AsAPollWould_TogetherWithThePollsAndOnlyInARow()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, 1, new IOException("poll"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        // A command whose answer can't be read counts, as a poll's would; the read after it answers, which starts the
        // count over.
        await Assert.ThrowsAsync<InvalidDataException>(() => poller.RunCommandAsync(_ => throw new InvalidDataException("broken"), Cancel));
        await WaitForPollAsync(board, time, reads: 2);
        Assert.True(poller.State.IsConnected);

        // A command that failed otherwise (1), the read after it (2) and a command without an answer (3) lose the board.
        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.RunCommandAsync(_ => throw new InvalidOperationException("failed"), Cancel));
        await WaitForPollAsync(board, time, reads: 3);
        Assert.True(poller.State.IsConnected);
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(_ => throw new TimeoutException("command"), Cancel));

        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "command"), board.Steps);
        Assert.Equal(3, board.Reads);
        await stop.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_KeepABoardThatRefusesThreeQuickCommands_AndLoseOneThatDoesntAnswerThem(bool refused)
    {
        var time = new ManualTimeProvider();

        // The read after each command is still under way when the next command comes and cancels it.
        HangingRead[] reads = [new(), new(), new()];
        var board = new FakePolledBoard(1, reads[0], reads[1], reads[2]);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        Exception failure = refused ? new BoardRefusedException("refused") : new TimeoutException("no answer");

        for (var command = 0; command < 3; command++)
        {
            if (command > 0)
            {
                await WaitUntilAsync(() => reads[command - 1].Started);
            }

            await Assert.ThrowsAsync(failure.GetType(), () => poller.RunCommandAsync(_ => Task.FromException<int>(failure), Cancel));
        }

        Assert.True(reads[0].IsCancelled);
        Assert.True(reads[1].IsCancelled);
        Assert.Equal(refused, poller.State.IsConnected);
        if (refused)
        {
            Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);
        }
        else
        {
            Assert.Contains((BoardPollStep.Lost, "no answer"), board.Steps);
        }

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_CountOnlyTheCommandsWithoutAReadableAnswer_AndStartOverAtARefusal()
    {
        var time = new ManualTimeProvider();
        HangingRead[] reads = [new(), new(), new(), new(), new()];
        var board = new FakePolledBoard(1, reads[0], reads[1], reads[2], reads[3], reads[4]);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        static Task<int> NoAnswer(CancellationToken token) => Task.FromException<int>(new TimeoutException("no answer"));
        static Task<int> Unreadable(CancellationToken token) => Task.FromException<int>(new InvalidDataException("unreadable"));
        static Task<int> Refused(CancellationToken token) => Task.FromException<int>(new BoardRefusedException("refused"));

        // No answer (1), unreadable (2), then a refusal starts the count over (0), with no read in between.
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(NoAnswer, Cancel));
        await WaitUntilAsync(() => reads[0].Started);
        await Assert.ThrowsAsync<InvalidDataException>(() => poller.RunCommandAsync(Unreadable, Cancel));
        await WaitUntilAsync(() => reads[1].Started);
        await Assert.ThrowsAsync<BoardRefusedException>(() => poller.RunCommandAsync(Refused, Cancel));
        await WaitUntilAsync(() => reads[2].Started);

        // So two more without an answer (1, 2) keep the board, and only the third in a row loses it.
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(NoAnswer, Cancel));
        await WaitUntilAsync(() => reads[3].Started);
        await Assert.ThrowsAsync<InvalidDataException>(() => poller.RunCommandAsync(Unreadable, Cancel));
        Assert.True(poller.State.IsConnected);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);

        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(NoAnswer, Cancel));
        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "no answer"), board.Steps);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_StartTheCountOver_WhenACommandIsRefused_AfterFailedPolls()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, new IOException("poll"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        // Two failed polls (1, 2), then a refused command: the board answered (0).
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 2);
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 3);
        await Assert.ThrowsAsync<BoardRefusedException>(() => poller.RunCommandAsync(_ => throw new BoardRefusedException("refused"), Cancel));

        // The read at once after it fails (1), and the next poll (2): still connected. The third in a row loses it.
        await WaitForPollAsync(board, time, reads: 4);
        time.Advance(Interval);
        await WaitForPollAsync(board, time, reads: 5);
        Assert.True(poller.State.IsConnected);
        time.Advance(Interval);
        await WaitUntilAsync(() => !poller.State.IsConnected);
        Assert.Equal(6, board.Reads);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_NotCount_ACommandThatSentNothing_OrThatTheCallerCancelled()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, new IOException("poll"));
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        // Sent nothing: not counted, but the board is still read at once, and that read fails (1, then 2).
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(_ => throw new BoardNotConnectedException("not sent"), Cancel));
        await WaitForPollAsync(board, time, reads: 2);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(_ => throw new BoardNotConnectedException("not sent"), Cancel));
        await WaitForPollAsync(board, time, reads: 3);
        Assert.True(poller.State.IsConnected);

        // Cancelled by the caller: not counted, and no read.
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.RunCommandAsync(
            async token =>
            {
                await cancelled.CancelAsync();
                token.ThrowIfCancellationRequested();
                return 0;
            },
            cancelled.Token));
        Assert.True(poller.State.IsConnected);

        // So the board is lost by the next failed poll (3), not before it.
        time.Advance(Interval);
        await WaitUntilAsync(() => !poller.State.IsConnected);
        Assert.Equal(4, board.Reads);
        Assert.Equal([(BoardPollStep.Lost, "poll")], board.Steps.Where(s => s.Kind == BoardPollStep.Lost));
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_FinishACommand_WhenThePollerIsDisposedMeanwhile()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;

        poller.Dispose();
        answer.SetResult(2);

        await command;
        Assert.Equal(2, poller.State.Reading);

        // Disposing ends the run, as a cancel does.
        await run;
    }

    // Issue #396: the board's code closes its connection once DisposeAsync has ended, so the command under way finishes
    // first. Nothing new starts meanwhile.
    [Fact]
    public async Task Should_WaitForTheCommandUnderWay_BeforeDisposeEnds()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        var run = poller.RunAsync(Cancel);
        await WaitForPollAsync(board, time, reads: 1);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;

        var disposing = poller.DisposeAsync().AsTask();

        Assert.False(disposing.IsCompleted);
        var sent = false;
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(
            _ =>
            {
                sent = true;
                return Task.FromResult(3);
            },
            Cancel));
        Assert.False(sent);
        Assert.False(disposing.IsCompleted);

        answer.SetResult(2);
        await command;
        await disposing;
        await run;
        Assert.Equal(1, board.Reads);
    }

    // A command still waiting for the lock when the poller is disposed never goes out, and doesn't hang.
    [Fact]
    public async Task Should_RefuseACommandWaitingForTheLock_WhenDisposed()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        var run = poller.RunAsync(Cancel);
        await WaitForPollAsync(board, time, reads: 1);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;
        var sent = false;
        var waiting = poller.RunCommandAsync(
            _ =>
            {
                sent = true;
                return Task.FromResult(3);
            },
            Cancel);

        try
        {
            poller.Dispose();

            // Bounded, so a command that hangs fails the test instead of holding it up.
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => waiting.WaitAsync(TimeSpan.FromMinutes(1), Cancel));
            Assert.False(sent);
        }
        finally
        {
            answer.TrySetResult(2);
        }

        await first;
        await run;
    }

    // The wait is bounded: a command that outlasts DisposeWait meets the closed connection, and whatever that makes it
    // throw (here a disposed stream) reaches the caller as BoardNotConnectedException.
    [Fact]
    public async Task Should_StopWaitingAfterDisposeWait_AndRefuseWhatTheClosedConnectionThrows()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval, DisposeWait = TimeSpan.FromSeconds(5) };
        var run = poller.RunAsync(Cancel);
        await WaitForPollAsync(board, time, reads: 1);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;

        var disposing = poller.DisposeAsync().AsTask();
        await WaitUntilAsync(() => time.WaitsOnly(TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromSeconds(5));
        await disposing;

        answer.SetException(new ObjectDisposedException("stream"));
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => command);
        Assert.IsType<ObjectDisposedException>(error.InnerException);
        await run;
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);
    }

    [Fact]
    public async Task Should_RefuseACommand_AfterDispose()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        var run = poller.RunAsync(Cancel);
        await WaitForPollAsync(board, time, reads: 1);

        await poller.DisposeAsync();
        await run;

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => poller.RunCommandAsync(_ => Task.FromResult(3), Cancel));
        poller.Dispose();
    }

    private static readonly BoardModel Model = new("x", "Brand", "X1", new BoardCapabilities(1, 0, false), 1, 8);

    // A problem the poller tries again after its usual wait, e.g. another model at the address.
    private static readonly ConnectionProblem Mismatch = new(ConnectionProblemKind.WrongModel, Model);

    private static readonly ConnectionProblem Refused = new(ConnectionProblemKind.LoginRefused, Model);

    private static readonly ConnectionProblem AskedToWait = new(ConnectionProblemKind.AskedToWait, Model);

    [Fact]
    public async Task Should_ReportWhyTheBoardRefusedTheConnect_OnceForTheSameProblem()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(1));
        Assert.Equal([new PolledState<int>(false, 0, null, Mismatch)], board.Changes);
        Assert.Equal(Mismatch, poller.State.Problem);

        // The same refusal again: nothing new to report.
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(2));
        Assert.Equal(2, board.Connects);
        Assert.Single(board.Changes);

        await stop.CancelAsync();
        await run;
        Assert.Equal(Mismatch, poller.State.Problem);
    }

    [Fact]
    public async Task Should_ClearTheProblem_WhenTheNextConnectGetsNoAnswer()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(1));

        board.ConnectError = new TimeoutException("no answer");
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(2));

        Assert.Null(poller.State.Problem);
        Assert.Equal(new PolledState<int>(false, 0, null), board.Changes[^1]);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ClearTheProblem_AtTheFirstAnswer()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(1));

        board.ConnectError = null;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForPollAsync(board, time, reads: 1);

        Assert.Equal(new PolledState<int>(true, 1, time.GetUtcNow()), poller.State);
        Assert.Equal(poller.State, board.Changes[^1]);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ClearTheProblem_WhenForgotten()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(1));

        poller.Forget();

        Assert.Equal(new PolledState<int>(false, 0, null), poller.State);
        Assert.Equal(poller.State, board.Changes[^1]);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Should_ClearTheProblem_WhenRestartedWithANewLogin()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForConnectWaitAsync(board, time, TimeSpan.FromSeconds(1));

        board.HangConnect = true;
        poller.Restart();

        Assert.Null(poller.State.Problem);
        Assert.Equal(new PolledState<int>(false, 0, null), board.Changes[^1]);
        await WaitUntilAsync(() => board.Connects == 2);

        // A restart while nothing is wrong reports nothing.
        var reported = board.Changes.Count;
        poller.Restart();
        await WaitUntilAsync(() => board.Connects == 3);
        Assert.Equal(reported, board.Changes.Count);
        await stop.CancelAsync();
        await run;
    }

    // A refusal that comes as the login changes (here while the board's code reports the lost connection) is about the
    // old login, and is dropped.
    [Fact]
    public async Task Should_DropAProblem_FromAConnectBeforeARestart()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Mismatch) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        board.OnLost = () =>
        {
            board.ConnectError = null;
            board.HangConnect = true;
            poller.Restart();
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await WaitUntilAsync(() => board.Connects == 2);

        Assert.Null(poller.State.Problem);
        Assert.DoesNotContain(board.Changes, c => c.Problem is not null);
        await stop.CancelAsync();
        await run;
    }

    // A refusal from the board at the old address that comes after the move says nothing about the new one.
    [Fact]
    public async Task Should_DropAProblem_FromAConnectToTheBoardBefore()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var connect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        board.HeldConnect = connect;
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.Connects == 1);

        poller.Forget();
        connect.SetException(new ProblemException(Mismatch));
        await WaitUntilAsync(() => board.Steps.Any(s => s.Kind == BoardPollStep.Lost));

        Assert.Null(poller.State.Problem);
        Assert.DoesNotContain(board.Changes, c => c.Problem is not null);
        await stop.CancelAsync();
        await run;
    }

    // Issue #522: a refused login isn't tried again by itself, however long the clock runs. No timer even waits.
    [Fact]
    public async Task Should_NotTryARefusedLoginAgain_UntilRestartedOrAskedTo()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(Refused) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await WaitUntilAsync(() => board.Steps.Count(s => s.Kind == BoardPollStep.Held) == 1);
        Assert.Equal(0, time.PendingTimers);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Waiting);
        Assert.Equal(Refused, poller.State.Problem);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, board.Connects);

        // "Check again": exactly one attempt, at once, with the problem cleared until the board answers.
        poller.TryAgain();
        Assert.Null(poller.State.Problem);
        await WaitUntilAsync(() => board.Steps.Count(s => s.Kind == BoardPollStep.Held) == 2);
        Assert.Equal(2, board.Connects);
        Assert.Equal(Refused, poller.State.Problem);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, board.Connects);

        // A new login: at once, and it connects.
        board.ConnectError = null;
        poller.Restart();
        await WaitForPollAsync(board, time, reads: 1);
        Assert.Equal(3, board.Connects);
        Assert.Equal(new PolledState<int>(true, 1, time.GetUtcNow()), poller.State);

        await stop.CancelAsync();
        await run;
    }

    // "Check again" on a board that answers changes nothing.
    [Fact]
    public async Task Should_LeaveAConnectedBoard_WhenAskedToTryAgain()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        poller.TryAgain();

        Assert.True(poller.State.IsConnected);
        Assert.Equal(1, board.Connects);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Stopped);
        await stop.CancelAsync();
        await run;
    }

    // A login that worked, then refused (e.g. while the board restarts): one try every 10 minutes and nothing in between,
    // for 24 hours from the first refusal, then held as a login that never worked. The problem stays "wrong login".
    [Fact]
    public async Task Should_TryALoginThatWorked_EveryPause_ThenHoldItAfter24Hours()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        await LoseThenRefuseAsync(board, time);
        var first = board.Connects;
        Assert.Equal("600", board.Steps[^1].Detail);
        Assert.Equal(Refused, poller.State.Problem);

        // Nothing in between.
        time.Advance(BoardPoller<int>.AskedToWaitPause - TimeSpan.FromSeconds(1));
        Assert.Equal(first, board.Connects);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => board.Connects == first + 1 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));

        var steps = (int)(BoardPoller<int>.RefusedLoginRetries / BoardPoller<int>.AskedToWaitPause);
        for (var i = 2; i < steps; i++)
        {
            time.Advance(BoardPoller<int>.AskedToWaitPause);
            await WaitUntilAsync(() => board.Connects == first + i && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
            Assert.Equal(Refused, poller.State.Problem);
        }

        // 24 hours after the first refusal: held, no timer.
        time.Advance(BoardPoller<int>.AskedToWaitPause);
        await WaitUntilAsync(() => board.Connects == first + steps && board.Steps.Any(s => s.Kind == BoardPollStep.Held));
        Assert.Equal(0, time.PendingTimers);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(first + steps, board.Connects);
        Assert.Equal(Refused, poller.State.Problem);

        // "Check again" still sends one attempt, and holds again.
        poller.TryAgain();
        await WaitUntilAsync(() => board.Connects == first + steps + 1 && board.Steps.Count(s => s.Kind == BoardPollStep.Held) == 2);
        Assert.Equal(0, time.PendingTimers);

        await stop.CancelAsync();
        await run;
    }

    // A connect that works again ends the tries, and the 24 hours start over at the next refusal.
    [Fact]
    public async Task Should_StartThe24HoursOver_WhenTheLoginWorksInBetween()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        await LoseThenRefuseAsync(board, time);

        // 20 hours of refusals, then the board takes the login again.
        var tries = (int)(TimeSpan.FromHours(20) / BoardPoller<int>.AskedToWaitPause);
        for (var i = 1; i <= tries; i++)
        {
            var connects = board.Connects;
            time.Advance(BoardPoller<int>.AskedToWaitPause);
            await WaitUntilAsync(() => board.Connects == connects + 1 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
        }

        board.ConnectError = null;
        board.Answer(1);
        var reads = board.Reads;
        time.Advance(BoardPoller<int>.AskedToWaitPause);
        await WaitForPollAsync(board, time, reads + 1);
        Assert.True(poller.State.IsConnected);

        // Refused again: tried every 10 minutes past the first 24 hours' end.
        await LoseThenRefuseAsync(board, time);
        for (var i = 0; i < (int)(TimeSpan.FromHours(5) / BoardPoller<int>.AskedToWaitPause); i++)
        {
            var connects = board.Connects;
            time.Advance(BoardPoller<int>.AskedToWaitPause);
            await WaitUntilAsync(() => board.Connects == connects + 1 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
        }

        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Held);
        await stop.CancelAsync();
        await run;
    }

    // "Check again" while a login that worked waits for its next try: one attempt at once, and the 10 minutes again.
    // A new login (Restart) counts as one that never worked: held at once.
    [Fact]
    public async Task Should_TryOnceAtOnce_OnCheckAgain_AndHoldANewLoginAtOnce()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        await LoseThenRefuseAsync(board, time);
        var connects = board.Connects;

        poller.TryAgain();
        await WaitUntilAsync(() => board.Connects == connects + 1 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Held);

        poller.Restart();
        await WaitUntilAsync(() => board.Connects == connects + 2 && board.Steps.Any(s => s.Kind == BoardPollStep.Held));
        Assert.Equal(0, time.PendingTimers);

        await stop.CancelAsync();
        await run;
    }

    // From a board that answers polls: three failed polls lose it, and the connect after is refused. Ends when the
    // poller waits for the next try.
    private static async Task LoseThenRefuseAsync(FakePolledBoard board, ManualTimeProvider time)
    {
        board.ConnectError = new ProblemException(Refused);
        board.Answer(new IOException("a"), new IOException("b"), new IOException("c"));
        var reads = board.Reads;
        for (var i = 1; i <= 2; i++)
        {
            time.Advance(Interval);
            await WaitForPollAsync(board, time, reads + i);
        }

        var connects = board.Connects;
        time.Advance(Interval);
        await WaitUntilAsync(() => board.Steps[^1] == (BoardPollStep.Waiting, "1") && time.WaitsOnly(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => board.Connects == connects + 1 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
    }

    // A board that asks to wait gets one attempt after AskedToWaitPause, not the quick loop. "Check again" goes at once.
    [Fact]
    public async Task Should_WaitTheLongPause_WhenTheBoardAsksToWait()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1) { ConnectError = new ProblemException(AskedToWait) };
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);

        await WaitForConnectWaitAsync(board, time, BoardPoller<int>.AskedToWaitPause);
        Assert.Equal("600", board.Steps.Last(s => s.Kind == BoardPollStep.Waiting).Detail);
        time.Advance(BoardPoller<int>.AskedToWaitPause - TimeSpan.FromSeconds(1));
        Assert.Equal(1, board.Connects);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => board.Connects == 2 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));

        poller.TryAgain();
        await WaitUntilAsync(() => board.Connects == 3 && time.WaitsOnly(BoardPoller<int>.AskedToWaitPause));
        Assert.Equal(AskedToWait, poller.State.Problem);

        // Once the board stops asking, the quick loop starts over from 1 s.
        board.ConnectError = new TimeoutException("no answer");
        poller.TryAgain();
        await WaitUntilAsync(() => board.Connects == 4 && time.WaitsOnly(BoardPoller<int>.MinBackoff));

        await stop.CancelAsync();
        await run;
    }

    // Another board may be at a new address: the state is forgotten and reported so, and an answer from before that
    // is still on its way (here a command's) is dropped.
    [Fact]
    public async Task Should_ForgetTheState_AndDropAnAnswerFromBefore()
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = poller.RunCommandAsync(
            _ =>
            {
                started.SetResult();
                return answer.Task;
            },
            Cancel);
        await started.Task;

        poller.Forget();
        var forgotten = new PolledState<int>(false, 0, null);
        Assert.Equal(forgotten, poller.State);
        Assert.Equal(forgotten, board.Changes[^1]);

        // Forgotten again, e.g. a second move before an answer: nothing new to report.
        var reported = board.Changes.Count;
        poller.Forget();
        Assert.Equal(reported, board.Changes.Count);

        answer.SetResult(7);
        await command;
        Assert.Equal(forgotten, poller.State);
        Assert.DoesNotContain(board.Changes, c => c.Reading == 7);

        await stop.CancelAsync();
        await run;
    }

    // Until every connect failed and the poller waits this long for the next one.
    private static Task WaitForConnectWaitAsync(FakePolledBoard board, ManualTimeProvider time, TimeSpan wait)
    {
        return WaitUntilAsync(() => board.Connects > 0 && board.Steps.Count(s => s.Kind == BoardPollStep.Waiting) == board.Connects && time.WaitsOnly(wait));
    }

    // Until the board answered this many polls and the poller waits for the next one.
    private static Task WaitForPollAsync(FakePolledBoard board, ManualTimeProvider time, int reads)
    {
        return WaitUntilAsync(() => board.Reads == reads && time.WaitsOnly(Interval));
    }

    // Waits for the poller's own thread to get there; never asserts on how long that takes.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(5, Cancel);
        }
    }

    private sealed class FakePolledBoard : IPolledBoard<int>
    {
        private readonly Lock _lock = new();
        private readonly Queue<object> _answers;
        private readonly List<PolledState<int>> _changes = [];
        private readonly List<PolledState<int>> _readingChanges = [];
        private readonly List<(BoardPollStep Kind, string? Detail)> _steps = [];
        private readonly List<DateTimeOffset> _readTimes = [];
        private object _last = 0;
        private int _reads;
        private int _connects;

        private TimeProvider? _time;

        public FakePolledBoard(params object[] answers)
        {
            _answers = new Queue<object>(answers);
        }

        /// <summary>Answers for the next reads, in order; the last one is kept after them, as with the first ones.</summary>
        public void Answer(params object[] answers)
        {
            lock (_lock)
            {
                foreach (var answer in answers)
                {
                    _answers.Enqueue(answer);
                }
            }
        }

        /// <summary>Records when each read happened on this clock.</summary>
        public FakePolledBoard On(TimeProvider time)
        {
            _time = time;
            return this;
        }

        public List<DateTimeOffset> ReadTimes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _readTimes];
                }
            }
        }

        public bool FailConnect { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public int Connects => Volatile.Read(ref _connects);

        public List<PolledState<int>> Changes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _changes];
                }
            }
        }

        public List<PolledState<int>> ReadingChanges
        {
            get
            {
                lock (_lock)
                {
                    return [.. _readingChanges];
                }
            }
        }

        public List<(BoardPollStep Kind, string? Detail)> Steps
        {
            get
            {
                lock (_lock)
                {
                    return [.. _steps];
                }
            }
        }

        /// <summary>When true, a connect hangs until cancelled, and then fails with an IOException, not a cancellation.</summary>
        public bool HangConnect { get; set; }

        /// <summary>What a connect fails with, e.g. an exception that says why the board refused it; null: it doesn't.</summary>
        public Exception? ConnectError { get; set; }

        /// <summary>Called once, the first time the poller reports the connection lost, before the poller goes on.</summary>
        public Action? OnLost { get; set; }

        /// <summary>When set, a connect ends when the test ends this, whether or not it is cancelled.</summary>
        public TaskCompletionSource? HeldConnect { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connects);
            if (HangConnect)
            {
                var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => aborted.TrySetException(new IOException("The socket was closed.")));
                return aborted.Task;
            }

            if (HeldConnect is { } held)
            {
                return held.Task;
            }

            if (ConnectError is { } error)
            {
                return Task.FromException(error);
            }

            return FailConnect ? Task.FromException(new IOException("refused")) : Task.CompletedTask;
        }

        public Task<int> ReadAsync(CancellationToken cancellationToken)
        {
            object answer;
            lock (_lock)
            {
                if (_answers.Count > 0)
                {
                    _last = _answers.Dequeue();
                }

                answer = _last;
            }

            lock (_lock)
            {
                _readTimes.Add(_time?.GetUtcNow() ?? default);
            }

            Interlocked.Increment(ref _reads);
            return answer switch
            {
                HangingRead hanging => hanging.Start(cancellationToken),
                Exception ex => Task.FromException<int>(ex),
                _ => Task.FromResult((int)answer),
            };
        }

        public void OnStateChanged(PolledState<int> state)
        {
            lock (_lock)
            {
                _changes.Add(state);
            }
        }

        public void OnReadingChanged(PolledState<int> state)
        {
            lock (_lock)
            {
                _readingChanges.Add(state);
            }
        }

        public void OnStep(BoardPollStep kind, string? detail)
        {
            lock (_lock)
            {
                _steps.Add((kind, detail));
            }

            if (kind == BoardPollStep.Lost && OnLost is { } onLost)
            {
                OnLost = null;
                onLost();
            }
        }
    }

    /// <summary>A connect the board refused on purpose, saying why.</summary>
    private sealed class ProblemException : Exception, IConnectionProblemSource
    {
        public ProblemException(ConnectionProblem problem)
            : base(problem.ToString())
        {
            Problem = problem;
        }

        public ConnectionProblem? Problem { get; }
    }

    /// <summary>A read that waits until the test ends it, as a request to a board that doesn't answer.</summary>
    private sealed class HangingRead
    {
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        /// <param name="honourCancel">Whether it gives up when the poll is cancelled, as a real request does.</param>
        public HangingRead(bool honourCancel = true)
        {
            HonourCancel = honourCancel;
        }

        public bool HonourCancel { get; }

        public bool Started => Volatile.Read(ref _started) == 1;

        public bool IsCancelled => _result.Task.IsCanceled;

        public Task<int> Start(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _started, 1);
            if (HonourCancel)
            {
                cancellationToken.Register(() => _result.TrySetCanceled(cancellationToken));
            }

            return _result.Task;
        }

        public void Fail(Exception exception)
        {
            _result.TrySetException(exception);
        }
    }
}
