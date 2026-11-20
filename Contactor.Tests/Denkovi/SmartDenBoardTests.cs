using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Denkovi;

/// <summary>The smartDEN as the app sees it: its model, its state, its relays and its login.</summary>
public sealed class SmartDenBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_DescribeTheModel()
    {
        var model = Assert.Single(SmartDenModels.All);

        Assert.Equal("denkovi-smartden-ip-16r-mt", model.Id);
        Assert.Equal("Denkovi", model.Brand);
        Assert.Equal("smartDEN IP-16R-MT", model.ModelName);
        Assert.Equal(16, model.Capabilities.RelayCount);
        Assert.Equal(0, model.Capabilities.InputCount);
        Assert.False(model.Capabilities.ReportsChanges);
        Assert.Equal(502, model.DefaultPort);
        Assert.Equal(0, model.LoginMaxLength);

        // The week schedule it reads and turns off; the auto-reboot's pulses it can't see (section 8.7).
        Assert.Equal(new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true), model.Capabilities.OwnRules);
        Assert.True(SmartDenModels.IsSmartDen(model));
        Assert.False(SmartDenModels.IsSmartDen(Vm201Model.Model));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(2000, true)]
    [InlineData(6_553_500, true)]
    [InlineData(6_553_600, false)]
    [InlineData(50, false)]
    [InlineData(250, false)]
    public void Should_PulseForATenthOfASecond_UpToAnHourAndAHalf(int milliseconds, bool canDo)
    {
        Assert.Equal(canDo, SmartDenModels.Ip16RMt.Capabilities.Pulse!.CanDo(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Should_ShowTheStateAsABoardState()
    {
        var at = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

        var state = SmartDenBoard.ToBoardState(new SmartDenState(true, new SmartDenStatus((1UL << 15) | 1UL), at));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 16]), ChannelMask.None, at, OwnRules: null), state);
        Assert.Equal(BoardState.Initial, SmartDenBoard.ToBoardState(SmartDenState.Initial));
    }

    [Fact]
    public void Should_ShowTheWeekSchedule_AsATimerPerRelay_WithARowAsAUnit()
    {
        // Row 0 switches relays 1 and 2, row 4 relays 2 and 3, row 9 relay 16.
        var schedule = new SmartDenSchedule([new SmartDenRow(0, 0b011), new SmartDenRow(4, 0b110), new SmartDenRow(9, 0x8000)]);

        var rules = SmartDenBoard.ToBoardState(new SmartDenState(true, new SmartDenStatus(0, schedule), null)).OwnRules!;

        OwnRule Timer(int relay) => new(relay, OwnRuleKind.Timer);
        Assert.Equal([Timer(1), Timer(2), Timer(3), Timer(16)], rules);
        Assert.Equal([[Timer(1), Timer(2)], [Timer(2), Timer(3)], [Timer(16)]], rules.Units.Select(u => u.ToList()));

        // Relay 1's timer takes row 0 off, and relay 2's stays with row 4; relays 1 and 3 together take relay 2's too.
        Assert.Empty(rules.GoesWith(Timer(1)));
        Assert.Equal([Timer(2)], rules.GoesWith([Timer(1), Timer(3)]));
        Assert.Equal([Timer(1), Timer(3)], rules.GoesWith(Timer(2)));

        Assert.Equal(OwnRules.Empty, SmartDenBoard.ToOwnRules(SmartDenSchedule.Empty));
        Assert.Null(SmartDenBoard.ToBoardState(new SmartDenState(true, new SmartDenStatus(0), null)).OwnRules);
    }

    [Fact]
    public async Task Should_TurnOffARow_AndTellOfTheRulesChangeAlone()
    {
        await using var simulator = new SmartDenSimulator { TimeProvider = new ManualTimeProvider() };
        await simulator.SetRowAsync(new SmartDenSimulatedRow(7, Enabled: true, Relays: 0b11, TurnOn: true, Hour: 6, Minute: 0, WeekDays: 0x7F));
        simulator.Start();
        using var client = CreateClient(simulator.Port);
        var board = new SmartDenBoard(client, NullLogger<SmartDenBoard>.Instance);
        List<BoardState> rulesChanges = [];
        List<BoardState> stateChanges = [];
        board.OwnRulesChanged += (_, state) => rulesChanges.Add(state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);
        board.StateChanged += (_, state) => stateChanges.Add(state);

        Assert.Equal([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.Timer)], board.State.OwnRules);

        Assert.Equal([new OwnRule(2, OwnRuleKind.Timer)], await board.TurnOffOwnRulesAsync([new OwnRule(2, OwnRuleKind.Timer)], Cancel));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        Assert.Equal(OwnRules.Empty, Assert.Single(rulesChanges).OwnRules);
        Assert.Empty(stateChanges);

        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public async Task Should_RefuseRelaysAndPulsesItCantDo_BeforeAnythingIsSent()
    {
        using var client = CreateClient(1);
        var board = new SmartDenBoard(client, NullLogger<SmartDenBoard>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(17), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromMilliseconds(250), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(17), TimeSpan.FromSeconds(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(16), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.PulseAsync(ChannelMask.Of(16), TimeSpan.FromSeconds(1), Cancel));
    }

    [Fact]
    public async Task Should_ReturnOnlyTheRelaysTheBoardTook_AndNeverLetARefusalOut()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = new SmartDenSimulator { TimeProvider = boardTime, FailRelays = 0b0100 };
        simulator.Start();
        using var client = CreateClient(simulator.Port);
        var board = new SmartDenBoard(client, NullLogger<SmartDenBoard>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.PulseAsync(ChannelMask.Of([2, 3, 4]), TimeSpan.FromSeconds(2.5), Cancel));
        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);
        Assert.Contains(simulator.ReceivedRequests, r => r.EndsWith("00 06 00 01 00 19", StringComparison.Ordinal));
        boardTime.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(0UL, simulator.Relays);

        // Every relay refused: "didn't take the command", as every board says it, never the poller's refusal.
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(3), Cancel));
        Assert.IsNotType<BoardRefusedException>(error);
        Assert.True(board.State.IsConnected);

        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public void Should_TakeNoLogin_AndSaySoByTheBrand()
    {
        using var client = CreateClient(SmartDenApi.DefaultPort, host: "");
        var connection = new SmartDenConnection(client, Options.Create(new SmartDenOptions()));

        Assert.Null(connection.CheckLogin(BoardLogin.None));
        var problem = connection.CheckLogin(new BoardLogin("admin", "123456"));
        Assert.Equal(new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: "Denkovi"), problem);
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "")));
        Assert.Equal(BoardLogin.None, connection.Login);
        Assert.Equal(new BoardEndpoint("", 502), connection.DefaultEndpoint);
    }

    private static SmartDenClient CreateClient(int port, string host = "127.0.0.1")
    {
        return new SmartDenClient(
            Options.Create(new SmartDenOptions { Host = host, Port = port }),
            SmartDenModels.Ip16RMt,
            new ManualTimeProvider(),
            NullLogger<SmartDenClient>.Instance);
    }
}
