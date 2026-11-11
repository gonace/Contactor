using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Contactor.Vm201;

namespace Contactor.Tests.Vm201;

/// <summary>
/// The client against the simulator over real loopback sockets.
/// </summary>
public sealed class Vm201ClientTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Should_ConnectAndReportStatus_WhenBoardHasNoLogin()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetOutputsAsync(0x05);
        await using var run = ClientRun.Start(simulator);

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0x05, run.Client.State.Outputs);
        Assert.True(run.Client.State.IsOn(1));
        Assert.False(run.Client.State.IsOn(2));
        Assert.NotNull(run.Client.State.LastContactUtc);
    }

    [Fact]
    public async Task Should_LogIn_WhenBoardRequiresCredentials()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "secret");

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0, simulator.DeniedLoginCount);
    }

    [Fact]
    public async Task Should_StayDisconnectedAndRetry_WhenPasswordIsWrong()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "wrong");

        await WaitUntilAsync(() => simulator.DeniedLoginCount >= 2);

        Assert.False(run.Client.State.IsConnected);
        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => run.Client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_UpdateState_WhenTurningRelaysOnAndOff()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(Vm201Channel.Mask(3), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => run.Client.State.IsOn(3));

        await run.Client.TurnOffAsync(Vm201Channel.All, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => run.Client.State.Outputs == 0);

        Assert.Equal(0, simulator.Outputs);
    }

    [Fact]
    public async Task Should_ReceivePushedStatus_WhenBoardChangesByItself()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        List<Vm201State> changes = [];
        run.Client.StateChanged += (_, state) =>
        {
            lock (changes)
            {
                changes.Add(state);
            }
        };

        await simulator.SetOutputsAsync(0x81);
        await simulator.SetInputAsync(true);
        await WaitUntilAsync(() => run.Client.State.Input);

        Assert.Equal(0x81, run.Client.State.Outputs);
        lock (changes)
        {
            Assert.Contains(changes, s => s.Outputs == 0x81 && !s.Input);
        }
    }

    [Fact]
    public async Task Should_SendTimerDisable_WhenDisablingBoardTimers()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x0F);
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.Client.DisableBoardTimersAsync(Vm201Channel.All, TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => run.Client.State.TimerMask == 0);
        Assert.Equal(0, simulator.TimerMask);
    }

    [Fact]
    public async Task Should_AnswerStatusRequest()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        var before = run.Client.State.LastContactUtc;

        await run.Client.RequestStatusAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => run.Client.State.LastContactUtc > before);
    }

    [Fact]
    public async Task Should_ReconnectAndRestoreStatus_WhenConnectionDrops()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Client.StateChanged += (_, state) =>
        {
            if (!state.IsConnected)
            {
                lost.TrySetResult();
            }
        };

        simulator.DropConnections();
        await lost.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.False(run.Client.State.IsConnected);
        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => run.Client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));

        // Changed while the client was away; the board's status after the new login is the truth.
        await simulator.SetOutputsAsync(0x02);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0x02, run.Client.State.Outputs);
        Assert.True(simulator.ConnectionCount >= 2);
    }

    [Fact]
    public async Task Should_KeepLastKnownOutputs_WhenDisconnected()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetOutputsAsync(0x10);
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        await WaitUntilAsync(() => !run.Client.State.IsConnected);

        Assert.Equal(0x10, run.Client.State.Outputs);
        Assert.NotNull(run.Client.State.LastContactUtc);
    }

    [Fact]
    public async Task Should_Reconnect_WhenBoardGoesSilentWithoutClosing()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator, keepAliveSeconds: 1);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        simulator.Unresponsive = true;
        await WaitUntilAsync(() => !run.Client.State.IsConnected);

        simulator.Unresponsive = false;
        await WaitUntilAsync(() => run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendKeepAlive()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator, keepAliveSeconds: 1);

        await WaitUntilAsync(() => simulator.ReceivedCommands.Count(p => p.Command == Vm201Command.StatusRequest) >= 2);

        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_FailCommands_WhenNeverConnected()
    {
        var client = CreateClient(port: 1);

        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => client.TurnOffAsync(0x01, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => client.RequestStatusAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<Vm201NotConnectedException>(() => client.DisableBoardTimersAsync(0xFF, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_KeepConnection_WhenStateSubscriberThrows()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        run.Client.StateChanged += (_, _) => throw new InvalidOperationException("Subscriber bug");
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await simulator.SetOutputsAsync(0x40);

        await WaitUntilAsync(() => run.Client.State.Outputs == 0x40);
        Assert.Equal(1, simulator.ConnectionCount);
    }

    [Fact]
    public async Task Should_SayGoodbye_WhenStopped()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.DisposeAsync();

        await WaitUntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.Closed));
        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_Throw_WhenRunTwice()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Client.RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData(" ", "", "")]
    [InlineData("127.0.0.1", "toolongname", "")]
    [InlineData("127.0.0.1", "", "lösenord")]
    public void Should_RejectOptions_WhenTheyCannotWork(string host, string username, string password)
    {
        var options = new Vm201Options { Host = host, Username = username, Password = password };

        Assert.Throws<ArgumentException>(
            () => new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance));
    }

    [Fact]
    public async Task Should_TraceTheConnectionAndEveryPacket()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(simulator, traced: trace.Add);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(Vm201Channel.Mask(1), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => trace.Has(Vm201TraceKind.Sent, Vm201Command.On));

        var entries = trace.Entries;
        Assert.Equal(Vm201TraceKind.Connecting, entries[0].Kind);
        Assert.Equal($"127.0.0.1:{simulator.Port}", entries[0].Detail);
        Assert.Equal((Vm201TraceKind.Received, Vm201Command.LoggedIn), (entries[1].Kind, entries[1].Command));
        Assert.Equal(9, entries.Count(e => e.Kind == Vm201TraceKind.Received && e.Command == Vm201Command.Name));
        var on = entries.Single(e => e.Kind == Vm201TraceKind.Sent && e.Command == Vm201Command.On);
        Assert.Equal(new byte[] { 0x02, 0x06, 0x4F, 0x01, 0xA8, 0x03 }, on.Frame.ToArray());
    }

    [Fact]
    public async Task Should_TraceTheLoginWithoutItsContent()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "secret", traced: trace.Add);

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        var login = trace.Entries.Where(e => e.Command is Vm201Command.Username or Vm201Command.Password).ToList();
        Assert.Equal(2, login.Count);
        Assert.All(login, e => Assert.Equal(3, e.Frame.Length));
        Assert.All(login, e => Assert.Equal(0, e.Data.Length));
    }

    [Fact]
    public async Task Should_TraceWhyTheConnectionEnded_WhenThePasswordIsWrong()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "wrong", traced: trace.Add);

        await WaitUntilAsync(() => trace.Entries.Any(e => e.Kind == Vm201TraceKind.Reconnecting));

        var entries = trace.Entries;
        Assert.Contains(entries, e => e.Kind == Vm201TraceKind.Received && e.Command == Vm201Command.AccessDenied);
        Assert.Contains(entries, e => e.Kind == Vm201TraceKind.ConnectionLost && e.Detail!.Contains("rejected", StringComparison.Ordinal));
        Assert.Equal("1", entries.First(e => e.Kind == Vm201TraceKind.Reconnecting).Detail);
    }

    [Fact]
    public async Task Should_KeepTheConnection_WhenATraceSubscriberFails()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator, traced: (_, _) => throw new InvalidOperationException("A bug."));

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(Vm201Channel.Mask(2), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => run.Client.State.IsOn(2));
    }

    private static Vm201Client CreateClient(int port, string username = "", string password = "", int keepAliveSeconds = 15)
    {
        var options = new Vm201Options
        {
            Host = "127.0.0.1",
            Port = port,
            Username = username,
            Password = password,
            KeepAliveSeconds = keepAliveSeconds,
        };
        return new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
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

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;

        private ClientRun(Vm201Client client, CancellationTokenSource cts, Task task)
        {
            Client = client;
            _cts = cts;
            _task = task;
        }

        public Vm201Client Client { get; }

        public static ClientRun Start(
            Vm201Simulator simulator,
            string username = "",
            string password = "",
            int keepAliveSeconds = 15,
            EventHandler<Vm201TraceEntry>? traced = null)
        {
            var client = CreateClient(simulator.Port, username, password, keepAliveSeconds);
            client.Traced += traced;
            var cts = new CancellationTokenSource();
            return new ClientRun(client, cts, client.RunAsync(cts.Token));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(WaitTimeout);
            _cts.Dispose();
        }
    }

    private sealed class TraceLog
    {
        private readonly Lock _lock = new();
        private readonly List<Vm201TraceEntry> _entries = [];

        public IReadOnlyList<Vm201TraceEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public void Add(object? sender, Vm201TraceEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        public bool Has(Vm201TraceKind kind, byte command)
        {
            return Entries.Any(e => e.Kind == kind && e.Command == command);
        }
    }
}
