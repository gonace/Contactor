using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Velleman;

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

    private const string HeldText = "Not trying again until a new login is saved or a person checks again";

    // Issue #537: as the polled boards, a refused login isn't tried again by itself, however long the clock runs.
    [Fact]
    public async Task Should_StayDisconnected_AndTryAgainOnlyWhenAsked_WhenThePasswordIsWrong()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        var time = new ManualTimeProvider();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "wrong", traced: trace.Add, time: time);

        await WaitUntilAsync(() => simulator.DeniedLoginCount == 1 && Held(trace) == 1);
        Assert.Equal(0, time.PendingTimers);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, simulator.DeniedLoginCount);
        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), run.Client.State.Problem);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(trace.Entries, e => e.Kind == Vm201TraceKind.Reconnecting);

        // "Check again", through the connection as the screens call it: one attempt, and held again.
        new Vm201Connection(run.Client, Options.Create(new Vm201Options())).TryAgain();
        await WaitUntilAsync(() => simulator.DeniedLoginCount == 2 && Held(trace) == 2);
        Assert.Equal(0, time.PendingTimers);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, simulator.DeniedLoginCount);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), run.Client.State.Problem);

        // A new login: tried at once.
        run.Client.SetLogin(Vm201Login.TryCreate("admin", "secret", out _)!);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(2, simulator.DeniedLoginCount);
        Assert.Null(run.Client.State.Problem);

        // "Check again" leaves a connected board alone.
        var connections = simulator.ConnectionCount;
        run.Client.TryAgain();
        await SendMarkerAsync(run.Client, simulator);
        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(connections, simulator.ConnectionCount);
        Assert.DoesNotContain(trace.Entries, e => e.Kind == Vm201TraceKind.ConnectionLost && e.Detail == Vm201Client.StoppedByApp);
    }

    // A login that worked before may be refused for a while: tried every 10 minutes for 24 hours, then held, as on the
    // polled boards.
    [Fact]
    public async Task Should_TryALoginThatWorkedBefore_Every10MinutesFor24Hours_ThenHoldIt()
    {
        var before = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        before.Start();
        var port = before.Port;
        var time = new ManualTimeProvider();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(before, username: "admin", password: "secret", traced: trace.Add, time: time);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        // The board's password is changed on the board itself.
        await before.DisposeAsync();
        await WaitUntilAsync(() => trace.Entries.Any(e => e.Kind == Vm201TraceKind.Reconnecting) && time.WaitsOnly(TimeSpan.FromSeconds(1)));
        await using var after = new Vm201Simulator("admin", "changed", NullLogger.Instance);
        after.Start(port);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => after.DeniedLoginCount == 1 && time.WaitsOnly(Vm201Client.RefusedLoginPause));
        Assert.Equal("600", trace.Entries.Last(e => e.Kind == Vm201TraceKind.Reconnecting).Detail);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), run.Client.State.Problem);

        // Nothing in between.
        time.Advance(Vm201Client.RefusedLoginPause - TimeSpan.FromSeconds(1));
        Assert.Equal(1, after.DeniedLoginCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => after.DeniedLoginCount == 2 && time.WaitsOnly(Vm201Client.RefusedLoginPause));

        var steps = (int)(Vm201Client.RefusedLoginRetries / Vm201Client.RefusedLoginPause);
        for (var i = 3; i <= steps; i++)
        {
            time.Advance(Vm201Client.RefusedLoginPause);
            await WaitUntilAsync(() => after.DeniedLoginCount == i && time.WaitsOnly(Vm201Client.RefusedLoginPause));
        }

        // 24 hours after the first refusal: held, no timer.
        Assert.Equal(0, Held(trace));
        time.Advance(Vm201Client.RefusedLoginPause);
        await WaitUntilAsync(() => after.DeniedLoginCount == steps + 1 && Held(trace) == 1);
        Assert.Equal(0, time.PendingTimers);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(steps + 1, after.DeniedLoginCount);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), run.Client.State.Problem);

        // "Check again" still sends one attempt, and holds again.
        run.Client.TryAgain();
        await WaitUntilAsync(() => after.DeniedLoginCount == steps + 2 && Held(trace) == 2);
        Assert.Equal(0, time.PendingTimers);
    }

    // Without a refused login, a lost connection is tried again with the usual backoff: 1 s, 2 s, 4 s.
    [Fact]
    public async Task Should_ReconnectWithBackoff_WhenTheConnectionDropsWithoutARefusal()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var time = new ManualTimeProvider();
        var trace = new TraceLog();
        await using var run = ClientRun.Start(simulator, traced: trace.Add, time: time);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            var wait = TimeSpan.FromSeconds(seconds);
            await WaitUntilAsync(() => Waits(trace, seconds) == 1 && time.WaitsOnly(wait));
            if (seconds == 4)
            {
                simulator.RejectConnections = false;
            }

            time.Advance(wait);
        }

        await WaitUntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(0, Held(trace));
        Assert.Equal(0, simulator.DeniedLoginCount);
        Assert.Null(run.Client.State.Problem);
    }

    [Fact]
    public async Task Should_SayTheLoginWasRefused_WithoutTheLogin_AndClearItForANewLogin()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "Zq7wrong");

        await WaitUntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), run.Client.State.Problem);
        Assert.DoesNotContain("Zq7wrong", run.Client.State.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Zq7wrong", Vm201Board.ToBoardState(run.Client.State).ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(run.Client.State.Problem, Vm201Board.ToBoardState(run.Client.State).Problem);

        run.Client.SetLogin(Vm201Login.TryCreate("admin", "secret", out _)!);

        Assert.Null(run.Client.State.Problem);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Problem);
    }

    [Fact]
    public async Task Should_SayALoginIsNeeded_WhenTheBoardAsksAndTheAppHasNone_AndClearItAtANewAddress()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator);

        await WaitUntilAsync(() => run.Client.State.Problem is not null);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), run.Client.State.Problem);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", 1));

        Assert.Equal(Vm201State.Initial, run.Client.State);
    }

    [Fact]
    public async Task Should_ReportNoProblem_ForABoardThatTakesTheLogin()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator, username: "admin", password: "secret");
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Null(run.Client.State.Problem);
    }

    // A board that refused the login and then stops answering (unplugged) no longer says why: "no contact" again.
    [Fact]
    public async Task Should_ClearTheProblem_WhenTheBoardStopsAnswering()
    {
        var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        var lost = 0;
        await using var run = ClientRun.Start(simulator, username: "admin", password: "wrong", traced: (_, entry) =>
        {
            if (entry.Kind == Vm201TraceKind.ConnectionLost && entry.Detail != "The board rejected the user name or password.")
            {
                Interlocked.Increment(ref lost);
            }
        });
        await WaitUntilAsync(() => run.Client.State.Problem is not null);

        // The refusal is held: "Check again" is what tries the board once more.
        await simulator.DisposeAsync();
        run.Client.TryAgain();

        await WaitUntilAsync(() => Volatile.Read(ref lost) > 0);
        Assert.Null(run.Client.State.Problem);
        Assert.False(run.Client.State.IsConnected);
    }

    // A refusal that comes as the login changes is about the old login: the new one's connection says its own.
    [Fact]
    public async Task Should_DropARefusal_ThatComesAsTheLoginChanges()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        var changed = 0;
        var problemsAfter = 0;
        Vm201Client? client = null;
        await using var run = ClientRun.Start(simulator, username: "admin", password: "wrong", traced: (_, entry) =>
        {
            // The refusal's trace comes before the client records it: the login changes right there.
            if (Volatile.Read(ref client) is { } watched && entry.Detail == "The board rejected the user name or password." && Interlocked.Exchange(ref changed, 1) == 0)
            {
                watched.SetLogin(Vm201Login.TryCreate("admin", "secret", out LoginProblem? _)!);
            }
        });
        run.Client.StateChanged += (_, state) =>
        {
            if (Volatile.Read(ref changed) == 1 && state.Problem is not null)
            {
                Interlocked.Increment(ref problemsAfter);
            }
        };
        Volatile.Write(ref client, run.Client);

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0, Volatile.Read(ref problemsAfter));
        Assert.Null(run.Client.State.Problem);
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

        await run.Client.TurnOffTimersAsync(Vm201Channel.All, TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => run.Client.State.TimerMask == 0);
        Assert.Equal(0, simulator.TimerMask);
    }

    [Fact]
    public async Task Should_TurnOffOnlyTheMaskedTimers_AndLeaveTheOutputs()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x15);
        await simulator.SetOutputsAsync(0x86);
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State is { IsConnected: true, TimerMask: 0x15 });

        await run.Client.TurnOffTimersAsync(0x04, TestContext.Current.CancellationToken);

        // The status that follows carries the new mask, and only bit 3 went.
        await WaitUntilAsync(() => run.Client.State.TimerMask == 0x11);
        Assert.Equal(0x86, run.Client.State.Outputs);
        Assert.Equal(0x11, simulator.TimerMask);
        Assert.Equal(0x86, simulator.Outputs);
        // The command is on its way when the call returns; the simulator takes it a moment later.
        await WaitUntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.TimerDisable));
        var sent = Assert.Single(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        Assert.Equal(new byte[] { 0x04 }, sent.Data.ToArray());
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
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));

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

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0x01, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOffAsync(0x01, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.RequestStatusAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOffTimersAsync(0xFF, TestContext.Current.CancellationToken));
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
    [InlineData("127.0.0.1", "toolongname", "")]
    [InlineData("127.0.0.1", "", "lösenord")]
    public void Should_RejectOptions_WhenTheyCannotWork(string host, string username, string password)
    {
        var options = new Vm201Options { Host = host, Username = username, Password = password };

        Assert.Throws<ArgumentException>(
            () => new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance));
    }

    // A new installation gets the board's address in the setup guide: the app starts without one.
    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        var client = new Vm201Client(Options.Create(new Vm201Options()), TimeProvider.System, NullLogger<Vm201Client>.Instance);

        Assert.False(client.Endpoint.HasHost);
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

        await WaitUntilAsync(() => trace.Entries.Any(e => e.Kind == Vm201TraceKind.Held));

        var entries = trace.Entries;
        Assert.Contains(entries, e => e.Kind == Vm201TraceKind.Received && e.Command == Vm201Command.AccessDenied);
        Assert.Contains(entries, e => e.Kind == Vm201TraceKind.ConnectionLost && e.Detail!.Contains("rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Kind == Vm201TraceKind.Reconnecting);
        Assert.Equal(HeldText, Vm201TraceText.Describe(entries.First(e => e.Kind == Vm201TraceKind.Held)).Text);
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

    [Fact]
    public async Task Should_NotSend_WhenCommandIsCancelledWhileTheConnectionIsBusy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        using var gate = new WriterGate();
        await using var run = ClientRun.Start(simulator, traced: gate.Traced);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        var hold = run.Client.TurnOffAsync(WriterGate.HoldMask, ct);
        await gate.Held.WaitAsync(WaitTimeout, ct);
        using var cts = new CancellationTokenSource();
        var cancelled = run.Client.TurnOnAsync(0x02, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Release();
        await hold;
        await SendMarkerAsync(run.Client, simulator);

        Assert.Equal([(Vm201Command.Off, WriterGate.HoldMask), (Vm201Command.On, Marker)], Sent(simulator));
        Assert.Equal(Marker, simulator.Outputs);
    }

    [Fact]
    public async Task Should_NotSend_WhenCommandIsCancelledBeforeTheCall()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Client.TurnOnAsync(0x02, cts.Token));
        await SendMarkerAsync(run.Client, simulator);

        Assert.Equal([(Vm201Command.On, Marker)], Sent(simulator));
    }

    [Fact]
    public async Task Should_ReportSuccess_WhenCancelledAfterTheWriterTookTheCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        using var gate = new WriterGate();
        await using var run = ClientRun.Start(simulator, traced: gate.Traced);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        using var cts = new CancellationTokenSource();
        var hold = run.Client.TurnOffAsync(WriterGate.HoldMask, cts.Token);
        await gate.Held.WaitAsync(WaitTimeout, ct);
        await cts.CancelAsync();
        gate.Release();

        await hold;
        await SendMarkerAsync(run.Client, simulator);
        Assert.Equal([(Vm201Command.Off, WriterGate.HoldMask), (Vm201Command.On, Marker)], Sent(simulator));
    }

    [Fact]
    public async Task Should_ThrowCancelled_WhenCancelledBeforeTheCallWhileNeverConnected()
    {
        var client = CreateClient(port: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TurnOnAsync(0x01, cts.Token));
    }

    [Fact]
    public async Task Should_SendTheOtherCommandsInOrder_WhenOneIsCancelledInTheQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        using var gate = new WriterGate();
        await using var run = ClientRun.Start(simulator, traced: gate.Traced);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        var hold = run.Client.TurnOffAsync(WriterGate.HoldMask, ct);
        await gate.Held.WaitAsync(WaitTimeout, ct);
        using var cts = new CancellationTokenSource();
        var first = run.Client.TurnOnAsync(0x01, ct);
        var cancelled = run.Client.TurnOnAsync(0x02, cts.Token);
        var third = run.Client.TurnOffTimersAsync(0x04, ct);
        var fourth = run.Client.TurnOffAsync(0x01, ct);
        await cts.CancelAsync();
        gate.Release();
        await Task.WhenAll(hold, first, third, fourth);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await SendMarkerAsync(run.Client, simulator);

        Assert.Equal(
            [
                (Vm201Command.Off, WriterGate.HoldMask),
                (Vm201Command.On, (byte)0x01),
                (Vm201Command.TimerDisable, (byte)0x04),
                (Vm201Command.Off, (byte)0x01),
                (Vm201Command.On, Marker),
            ],
            Sent(simulator));
    }

    [Fact]
    public async Task Should_ReportWhatWasSent_WhenCancelledWhileTheWriterTakesTheCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        using var gate = new WriterGate();
        await using var run = ClientRun.Start(simulator, traced: gate.Traced);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        List<byte> reportedSent = [];
        for (var i = 1; i <= 100; i++)
        {
            var hold = run.Client.TurnOffAsync(WriterGate.HoldMask, ct);
            await gate.Held.WaitAsync(WaitTimeout, ct);
            using var cts = new CancellationTokenSource();
            var command = run.Client.TurnOffTimersAsync((byte)i, cts.Token);

            // The writer takes the command as soon as it is released, while the caller cancels it.
            var release = Task.Run(gate.Release, ct);
            cts.Cancel();
            await release;
            await hold;
            try
            {
                await command;
                reportedSent.Add((byte)i);
            }
            catch (OperationCanceledException)
            {
                // Not sent; the check below proves it.
            }
        }

        await SendMarkerAsync(run.Client, simulator);

        var received = Sent(simulator).Where(p => p.Command == Vm201Command.TimerDisable).Select(p => p.Mask);
        Assert.Equal(reportedSent, received);
    }

    // Relay 7: no test above uses it for anything else.
    private const byte Marker = 0x40;

    // Every command sent before the marker has reached the simulator once the marker has: the client sends in order.
    private static async Task SendMarkerAsync(Vm201Client client, Vm201Simulator simulator)
    {
        await client.TurnOnAsync(Marker, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.On && p.Data.Span[0] == Marker));
    }

    // The commands the simulator got with a mask, in order; the keep-alive's status requests are left out.
    private static List<(byte Command, byte Mask)> Sent(Vm201Simulator simulator)
    {
        return [.. simulator.ReceivedCommands
            .Where(p => p.Command is Vm201Command.On or Vm201Command.Off or Vm201Command.TimerDisable)
            .Select(p => (p.Command, p.Data.Span[0]))];
    }

    private static int Held(TraceLog trace)
    {
        return trace.Entries.Count(e => e.Kind == Vm201TraceKind.Held);
    }

    private static int Waits(TraceLog trace, int seconds)
    {
        var detail = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return trace.Entries.Count(e => e.Kind == Vm201TraceKind.Reconnecting && e.Detail == detail);
    }

    private static Vm201Client CreateClient(int port, string username = "", string password = "", int keepAliveSeconds = 15, TimeProvider? time = null)
    {
        var options = new Vm201Options
        {
            Host = "127.0.0.1",
            Port = port,
            Username = username,
            Password = password,
            KeepAliveSeconds = keepAliveSeconds,
        };
        return new Vm201Client(Options.Create(options), time ?? TimeProvider.System, NullLogger<Vm201Client>.Instance);
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
            EventHandler<Vm201TraceEntry>? traced = null,
            TimeProvider? time = null)
        {
            var client = CreateClient(simulator.Port, username, password, keepAliveSeconds, time);
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

    /// <summary>
    /// Holds the client's writer: the trace of a sent <see cref="HoldMask"/> OFF is raised on the writer, so the writer
    /// waits there until <see cref="Release"/>, and the commands behind it wait in the queue.
    /// </summary>
    private sealed class WriterGate : IDisposable
    {
        public const byte HoldMask = 0x80;

        private readonly SemaphoreSlim _release = new(0);

        public SemaphoreSlim Held { get; } = new(0);

        public void Traced(object? sender, Vm201TraceEntry entry)
        {
            if (entry.Kind == Vm201TraceKind.Sent && entry.Command == Vm201Command.Off && entry.Data.SequenceEqual([HoldMask]))
            {
                Held.Release();

                // Bounded, so a failing test ends instead of holding the client for ever.
                _release.Wait(WaitTimeout);
            }
        }

        public void Release()
        {
            _release.Release();
        }

        public void Dispose()
        {
            _release.Dispose();
            Held.Dispose();
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
