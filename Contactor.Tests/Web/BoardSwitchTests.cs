using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>The one board the app talks to, and switching it while the app runs.</summary>
public sealed class BoardSwitchTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_HaveNoRelaysAndRefuseCommands_BeforeABoardIsChosen()
    {
        await using var boards = new BoardSwitch(NullLogger<BoardSwitch>.Instance);

        Assert.Null(boards.Current);
        Assert.Same(BoardSwitch.NoBoard, boards.Capabilities);
        Assert.Same(BoardState.Initial, boards.State);
        Assert.False(boards.Endpoint.HasHost);
        Assert.True(boards.Login.IsNone);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => boards.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => boards.TurnOffAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => boards.RefreshAsync(Cancel));
        Assert.Throws<InvalidOperationException>(() => boards.SetEndpoint(new BoardEndpoint("127.0.0.1", 9760)));
        Assert.Throws<InvalidOperationException>(() => boards.SetLogin(BoardLogin.None));
    }

    [Fact]
    public async Task Should_PassEverythingToTheChosenBoard()
    {
        await using var boards = new BoardSwitch(NullLogger<BoardSwitch>.Instance);
        var parts = new Parts(relayCount: 4);

        await boards.SwitchToAsync(parts.Build(Vm204Model.Model));
        await boards.TurnOnAsync(ChannelMask.Of(2), Cancel);
        boards.SetEndpoint(new BoardEndpoint("10.0.0.9", 80));

        Assert.Equal(Vm204Model.Model, boards.Model);
        Assert.Same(parts.Board.Capabilities, boards.Capabilities);
        Assert.True(boards.State.IsOn(2));
        Assert.Equal(new BoardEndpoint("10.0.0.9", 80), parts.Connection.Endpoint);
        Assert.Equal(parts.Connection.Endpoint, boards.Endpoint);
    }

    // The new board is unknown until it answers, as at a new address, so the screens and the recorder start afresh.
    [Fact]
    public async Task Should_ReportTheNewBoardsState_AtTheSwitch_AndNothingMoreFromTheOldOne()
    {
        await using var boards = new BoardSwitch(NullLogger<BoardSwitch>.Instance);
        var old = new Parts();
        var next = new Parts(connected: false);
        var states = new List<BoardState>();
        var traffic = new List<BoardTraceEntry>();
        boards.StateChanged += (_, state) => states.Add(state);
        boards.Traced += (_, entry) => traffic.Add(entry);
        await boards.SwitchToAsync(old.Build(Vm201Model.Model));
        states.Clear();

        await boards.SwitchToAsync(next.Build(Vm204Model.Model));
        old.Board.SetRelay(1, on: true);
        old.Traffic.Trace("old");
        next.Board.SetRelay(2, on: true);
        next.Traffic.Trace("new");

        Assert.Equal(2, states.Count);
        Assert.False(states[0].HasAnswered);
        Assert.True(states[1].IsOn(2));
        Assert.Equal("new", Assert.Single(traffic).Text);
    }

    [Fact]
    public async Task Should_RunTheChosenBoard_AndTheNextOneAfterASwitch()
    {
        await using var boards = new BoardSwitch(NullLogger<BoardSwitch>.Instance);
        var old = new Parts();
        var next = new Parts();
        var prepared = new List<BoardModel>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = boards.RunAsync(
            (board, _) =>
            {
                lock (prepared)
                {
                    prepared.Add(board.Model);
                }

                return Task.CompletedTask;
            },
            stop.Token);

        // Nothing to run yet: it waits for a board.
        await Task.Delay(100, Cancel);
        Assert.False(run.IsCompleted);
        await boards.SwitchToAsync(old.Build(Vm201Model.Model));
        await WaitUntilAsync(() => old.Connection.Running && old.Extra.Started);

        await boards.SwitchToAsync(next.Build(Vm204Model.Model));
        await WaitUntilAsync(() => next.Connection.Running && next.Extra.Started);

        // The old one stopped, what only it had first, and was disposed once its run ended.
        Assert.False(old.Connection.Running);
        Assert.True(old.Extra.Stopped);
        await WaitUntilAsync(() => old.Connection.Disposed);
        Assert.False(next.Connection.Disposed);
        lock (prepared)
        {
            Assert.Equal([Vm201Model.Model, Vm204Model.Model], prepared);
        }

        await stop.CancelAsync();
        await run;
        Assert.True(next.Extra.Stopped);
        Assert.False(next.Connection.Disposed);
        await boards.DisposeAsync();
        Assert.True(next.Connection.Disposed);
    }

    [Fact]
    public async Task Should_DisposeTheOldBoardAtOnce_WhenItIsNotRunning()
    {
        await using var boards = new BoardSwitch(NullLogger<BoardSwitch>.Instance);
        var old = new Parts();

        await boards.SwitchToAsync(old.Build(Vm201Model.Model));
        await boards.SwitchToAsync(new Parts().Build(Vm204Model.Model));

        Assert.True(old.Connection.Disposed);
        Assert.False(old.Extra.Started);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }

    /// <summary>A board in memory, as a model's own code builds it: its adapters and one thing only it has.</summary>
    private sealed class Parts
    {
        public Parts(int relayCount = 8, bool connected = true)
        {
            Board = new FakeRelayBoard(relayCount, connected);
        }

        public FakeRelayBoard Board { get; }

        public FakeConnection Connection { get; } = new();

        public FakeTraffic Traffic { get; } = new();

        public FakeExtra Extra { get; } = new();

        public ActiveBoard Build(BoardModel model)
        {
            var app = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .AddSingleton(TimeProvider.System)
                .BuildServiceProvider();
            var services = ActiveBoard.NewServices(model, app);
            // From factories, so the board's services dispose them as they would their own.
            services.AddSingleton(_ => Board);
            services.AddSingleton(_ => Connection);
            services.AddSingleton(_ => Traffic);
            return ActiveBoard.Build<FakeRelayBoard, FakeConnection, FakeTraffic>(model, services, _ => [Extra]);
        }
    }

    private sealed class FakeConnection : IBoardConnection, IDisposable
    {
        private volatile bool _running;
        private volatile bool _disposed;

        public bool Running => _running;

        public bool Disposed => _disposed;

        public BoardEndpoint DefaultEndpoint { get; } = new("192.168.1.50", 9760);

        public BoardLogin DefaultLogin => BoardLogin.None;

        public BoardEndpoint Endpoint { get; private set; } = new("192.168.1.50", 9760);

        public BoardLogin Login { get; private set; } = BoardLogin.None;

        public void SetEndpoint(BoardEndpoint endpoint)
        {
            Endpoint = endpoint;
        }

        public void SetLogin(BoardLogin login)
        {
            Login = login;
        }

        public string? CheckLogin(BoardLogin login)
        {
            return null;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _running = true;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                _running = false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    private sealed class FakeTraffic : IBoardTraffic
    {
        public event EventHandler<BoardTraceEntry>? Traced;

        public void Trace(string text)
        {
            Traced?.Invoke(this, new BoardTraceEntry(DateTimeOffset.UnixEpoch, BoardTraceDirection.Sent, text, null, false, false));
        }
    }

    private sealed class FakeExtra : IHostedService
    {
        private volatile bool _started;
        private volatile bool _stopped;

        public bool Started => _started;

        public bool Stopped => _stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped = true;
            return Task.CompletedTask;
        }
    }
}
