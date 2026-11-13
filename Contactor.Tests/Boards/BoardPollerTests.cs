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
            await WaitUntilAsync(() => board.Steps.Count(s => s.Kind == BoardPollStep.Waiting) == board.Connects && time.PendingTimers == 1);
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
        await WaitUntilAsync(() => time.PendingTimers == 1);
        Assert.Equal(2, board.Reads);
        Assert.DoesNotContain(board.Steps, s => s.Kind == BoardPollStep.Lost);

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
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")) && time.PendingTimers == 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "2")) && time.PendingTimers == 1);

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
        await WaitUntilAsync(() => board.Steps.Contains((BoardPollStep.Waiting, "1")) && time.PendingTimers == 1);

        poller.PollNow();
        board.FailConnect = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForPollAsync(board, time, reads: 1);
        time.Advance(Interval);
        await WaitUntilAsync(() => board.Reads >= 2 && time.PendingTimers == 1);

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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_ReadTheBoardAtOnce_AfterACommandThatFailed(bool noAnswer)
    {
        var time = new ManualTimeProvider();
        var board = new FakePolledBoard(1, 7);
        var poller = new BoardPoller<int>(board, time) { PollInterval = Interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = poller.RunAsync(stop.Token);
        await WaitForPollAsync(board, time, reads: 1);
        Exception failure = noAnswer ? new TimeoutException("no answer") : new InvalidOperationException("refused");

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

        // A refused command (1), the read after it (2) and a command without an answer (3) lose the board.
        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.RunCommandAsync(_ => throw new InvalidOperationException("refused"), Cancel));
        await WaitForPollAsync(board, time, reads: 3);
        Assert.True(poller.State.IsConnected);
        await Assert.ThrowsAsync<TimeoutException>(() => poller.RunCommandAsync(_ => throw new TimeoutException("command"), Cancel));

        Assert.False(poller.State.IsConnected);
        Assert.Contains((BoardPollStep.Lost, "command"), board.Steps);
        Assert.Equal(3, board.Reads);
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
        await stop.CancelAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run);
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

        answer.SetResult(7);
        await command;
        Assert.Equal(forgotten, poller.State);
        Assert.DoesNotContain(board.Changes, c => c.Reading == 7);

        await stop.CancelAsync();
        await run;
    }

    // Until the board answered this many polls and the poller waits for the next one.
    private static Task WaitForPollAsync(FakePolledBoard board, ManualTimeProvider time, int reads)
    {
        return WaitUntilAsync(() => board.Reads == reads && time.PendingTimers == 1);
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

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connects);
            if (HangConnect)
            {
                var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => aborted.TrySetException(new IOException("The socket was closed.")));
                return aborted.Task;
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

        public void OnStep(BoardPollStep kind, string? detail)
        {
            lock (_lock)
            {
                _steps.Add((kind, detail));
            }
        }
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
