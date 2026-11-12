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
        private object _last = 0;
        private int _reads;
        private int _connects;

        public FakePolledBoard(params object[] answers)
        {
            _answers = new Queue<object>(answers);
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

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connects);
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

            Interlocked.Increment(ref _reads);
            return answer is Exception ex ? Task.FromException<int>(ex) : Task.FromResult((int)answer);
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
}
