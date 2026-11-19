using Contactor.Boards;
using Contactor.Boards.Velleman;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The VM201 as an <see cref="IRelayBoard"/>: its state and commands translated, and its status passed on.</summary>
public sealed class Vm201BoardTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HaveEightRelaysAndOneInput_AndAPulse()
    {
        Assert.Equal(new BoardCapabilities(8, 1, ReportsChanges: true, Pulse: Vm201Pulse.Ability, OwnRules: OwnRulesAbility.ReadsAndTurnsOff), Vm201Board.Vm201Capabilities);
        Assert.Equal(ChannelMask.FirstN(8), Vm201Board.Vm201Capabilities.AllRelays);
    }

    [Fact]
    public void Should_TranslateTheBoardsStatus()
    {
        var state = Vm201Board.ToBoardState(new Vm201State(true, 0b1000_0101, 0b0000_0010, true, At));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 3, 8]), ChannelMask.Of(1), At, OwnRules: new OwnRules([new OwnRule(2, OwnRuleKind.Timer)])), state);
        Assert.True(state.IsOn(8));
        Assert.False(state.IsOn(2));
        Assert.Equal(ChannelMask.None, Vm201Board.ToBoardState(Vm201State.Initial).Inputs);
    }

    [Fact]
    public void Should_ReportATimerRulePerTimerBit_InRelayOrder()
    {
        var state = Vm201Board.ToBoardState(new Vm201State(true, 0, 0b1001_0100, false, At));

        Assert.Equal(
            new OwnRules([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(5, OwnRuleKind.Timer), new OwnRule(8, OwnRuleKind.Timer)]),
            state.OwnRules);
        Assert.Equal(OwnRules.Empty, Vm201Board.ToBoardState(new Vm201State(true, 0xFF, 0, true, At)).OwnRules);
        Assert.Equal(ChannelMask.FirstN(8), Vm201Board.ToBoardState(new Vm201State(true, 0, 0xFF, false, At)).OwnRules?.Relays);
    }

    [Fact]
    public void Should_ReportNoRules_BeforeTheFirstAnswer()
    {
        Assert.Null(Vm201Board.ToBoardState(Vm201State.Initial).OwnRules);
        Assert.Null(Vm201Board.ToBoardState(Vm201State.Initial with { TimerMask = 0x01 }).OwnRules);
    }

    [Fact]
    public void Should_KeepTheRules_WhileDisconnected()
    {
        var state = Vm201Board.ToBoardState(new Vm201State(false, 0, 0b0000_0001, false, At));

        Assert.False(state.IsConnected);
        Assert.Equal(new OwnRules([new OwnRule(1, OwnRuleKind.Timer)]), state.OwnRules);
    }

    [Fact]
    public async Task Should_KeepTheRules_WhenTheConnectionIsLost()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x10);
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);
        Assert.Equal(new OwnRules([new OwnRule(5, OwnRuleKind.Timer)]), board.State.OwnRules);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        await WaitUntilAsync(() => !board.State.IsConnected);

        Assert.Equal(new OwnRules([new OwnRule(5, OwnRuleKind.Timer)]), board.State.OwnRules);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    // Another board at a new address: the old board's timers are not the new one's, so nothing is known until it answers.
    [Fact]
    public async Task Should_ForgetTheRules_AtANewAddress()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x10);
        await using var second = new Vm201Simulator();
        second.Start();
        second.RejectConnections = true;
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.OwnRules is not null);

        client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        Assert.Null(board.State.OwnRules);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    // The packet for relays 3 and 5, byte for byte as Contactor.Boards.Velleman/README.md gives it.
    [Fact]
    public void Should_EncodeTheTimerTurnOff_AsTheReadmeGivesIt()
    {
        Assert.Equal(new byte[] { 0x02, 0x06, 0x44, 0x14, 0xA0, 0x03 }, Vm201PacketCodec.Encode(Vm201Command.TimerDisable, 0x14));
    }

    [Fact]
    public async Task Should_TurnOffOnlyTheTimersAsked_AndReportTheNewRules()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0b0011_0101);
        await simulator.SetOutputsAsync(0b0000_0110);
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        BoardState? reported = null;
        // Written on the client's thread and read on the test's.
        board.StateChanged += (_, state) => Volatile.Write(ref reported, state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);
        var asked = new[] { new OwnRule(5, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer) };

        var taken = await board.TurnOffOwnRulesAsync(asked, Cancel);

        Assert.Equal([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(5, OwnRuleKind.Timer)], taken);
        // The command is on its way when the call returns; the simulator takes it a moment later.
        await WaitUntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.TimerDisable));
        var sent = Assert.Single(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        Assert.Equal(new byte[] { 0x14 }, sent.Data.ToArray());
        await WaitUntilAsync(() => Volatile.Read(ref reported)?.OwnRules?.Count == 2);
        Assert.Equal(new OwnRules([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(6, OwnRuleKind.Timer)]), board.State.OwnRules);
        Assert.Equal(ChannelMask.Of([2, 3]), board.State.Relays);
        Assert.Equal(0b0010_0001, simulator.TimerMask);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Theory]
    [InlineData(9, OwnRuleKind.Timer)]
    [InlineData(64, OwnRuleKind.Timer)]
    [InlineData(1, OwnRuleKind.InputLinkage)]
    [InlineData(2, OwnRuleKind.InputToggle)]
    [InlineData(3, OwnRuleKind.InputEdge)]
    public async Task Should_RefuseARuleTheBoardDoesntHave_BeforeSendingAnything(int relay, OwnRuleKind kind)
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0xFF);
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);

        // With a rule the board has first, so nothing of the list may go.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => board.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(relay, kind)], Cancel));

        await board.RefreshAsync(Cancel);
        await WaitUntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.StatusRequest));
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command == Vm201Command.TimerDisable);
        Assert.Equal(0xFF, simulator.TimerMask);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Fact]
    public async Task Should_SendNothing_ForNoRules()
    {
        // No board at all: had it sent anything, it would have thrown BoardNotConnectedException.
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        Assert.Empty(await board.TurnOffOwnRulesAsync([], Cancel));
    }

    [Fact]
    public async Task Should_RefuseANullRule()
    {
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => board.TurnOffOwnRulesAsync(null!, Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => board.TurnOffOwnRulesAsync([null!], Cancel));
    }

    [Fact]
    public void Should_StateItReadsAndTurnsOffItsRules()
    {
        Assert.Equal(OwnRulesAbility.ReadsAndTurnsOff, Vm201Board.Vm201Capabilities.OwnRulesOrCantTell);
    }

    [Fact]
    public void Should_PassTheProblemOn()
    {
        var problem = new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model);

        var state = Vm201Board.ToBoardState(Vm201State.Initial with { Problem = problem });

        Assert.Equal(BoardState.Initial with { Problem = problem }, state);
    }

    [Fact]
    public void Should_SendRelaysOneToEight_AndRefuseOthers()
    {
        Assert.Equal(0b1000_0001, Vm201Board.ToMask(ChannelMask.Of([1, 8])));
        Assert.Equal(0, Vm201Board.ToMask(ChannelMask.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.ToMask(ChannelMask.Of(9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.ToMask(ChannelMask.Of([1, 64])));
    }

    [Fact]
    public async Task Should_SwitchTheBoard_AndPassOnItsStatus()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        BoardState? reported = null;
        board.StateChanged += (_, state) => reported = state;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.TurnOnAsync(ChannelMask.Of([2, 4]), Cancel));
        await WaitUntilAsync(() => reported?.Relays == ChannelMask.Of([2, 4]));
        await board.TurnOffAsync(ChannelMask.Of(2), Cancel);
        await WaitUntilAsync(() => board.State.Relays == ChannelMask.Of(4));

        Assert.Equal(0b0000_1000, simulator.Outputs);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Fact]
    public async Task Should_PulseTheWholeMaskAtOnce()
    {
        await using var simulator = new Vm201Simulator { TimeProvider = new Web.ManualTimeProvider() };
        simulator.Start();
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([1, 8]), await board.PulseAsync(ChannelMask.Of([1, 8]), TimeSpan.FromMinutes(3), Cancel));

        await WaitUntilAsync(() => board.State.Relays == ChannelMask.Of([1, 8]));
        var pulse = Assert.Single(simulator.ReceivedCommands, p => p.Command == Vm201Command.Pulse);
        Assert.Equal(new byte[] { 0b1000_0001, 3, (byte)'m' }, pulse.Data.ToArray());
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(90.5)]
    [InlineData(360_000)]
    public async Task Should_RefuseALengthTheBoardCantSendExactly(double seconds)
    {
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(seconds), Cancel));
    }

    [Fact]
    public async Task Should_RefuseAPulseOfARelayTheBoardDoesntHave()
    {
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(9), TimeSpan.FromSeconds(2), Cancel));
    }

    [Fact]
    public async Task Should_GiveEverySubscriberTheState_WhenOneFails()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        board.StateChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        BoardState? reported = null;
        board.StateChanged += (_, state) => reported = state;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);

        await WaitUntilAsync(() => reported?.IsConnected == true);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Should_RefuseARelayTheBoardDoesntHave(int relay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.Vm201Capabilities.Relay(relay));
    }

    [Fact]
    public async Task Should_RefuseCommands_WithoutTheBoard()
    {
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(2), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOffOwnRulesAsync([new OwnRule(3, OwnRuleKind.Timer)], Cancel));
    }

    private static Vm201Client CreateClient(int port)
    {
        var options = new Vm201Options { Host = "127.0.0.1", Port = port };
        return new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
