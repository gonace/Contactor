using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The Waveshare boards as the app sees them: the five models, their state and their relays.</summary>
public sealed class WaveshareBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("waveshare-8", "Waveshare Modbus PoE ETH Relay", 8, 0)]
    [InlineData("waveshare-8b", "Waveshare Modbus PoE ETH Relay (B)", 8, 8)]
    [InlineData("waveshare-8c", "Waveshare Modbus PoE ETH Relay (C)", 8, 8)]
    [InlineData("waveshare-16", "Waveshare Modbus PoE ETH Relay 16CH", 16, 0)]
    [InlineData("waveshare-30", "Waveshare Modbus PoE ETH Relay 30CH", 30, 0)]
    public void Should_DescribeEachModel(string id, string name, int relays, int inputs)
    {
        var model = Assert.Single(WaveshareModels.All, m => m.Id == id);

        Assert.Equal(name, model.Name);
        var pulse = new PulseAbility(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3_276_700), TimeSpan.FromMilliseconds(100));
        Assert.Equal(new BoardCapabilities(relays, inputs, ReportsChanges: false, Pulse: pulse, OwnRules: inputs > 0 ? OwnRulesAbility.ReadsAndTurnsOff : OwnRulesAbility.None), model.Capabilities);
        Assert.Equal(4196, model.DefaultPort);
        Assert.False(model.HasLogin);
        Assert.Equal(inputs > 0, WaveshareModels.HasRelayModes(model));
    }

    [Fact]
    public void Should_TranslateTheState()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        var state = WaveshareBoard.ToBoardState(new WaveshareState(true, new WaveshareStatus(1UL << 29, 0b1000_0001), at));

        Assert.Equal(new BoardState(true, ChannelMask.Of(30), ChannelMask.Of([1, 8]), at), state);
        Assert.Equal(BoardState.Initial, WaveshareBoard.ToBoardState(WaveshareState.Initial));
    }

    // Relay n's mode is tied to input n: mode 1 linkage, 2 toggle, 3 edge; 0 (normal) is no rule.
    [Fact]
    public void Should_MapTheModes_ToOwnRules()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        var state = WaveshareBoard.ToBoardState(new WaveshareState(true, new WaveshareStatus(0, 0, [0, 1, 2, 3, 0, 0, 0, 1]), at));

        Assert.Equal(
            new OwnRules([new(2, OwnRuleKind.InputLinkage), new(3, OwnRuleKind.InputToggle), new(4, OwnRuleKind.InputEdge), new(8, OwnRuleKind.InputLinkage)]),
            state.OwnRules);
        Assert.Equal(ChannelMask.Of([2, 8]), state.Unavailable);
        Assert.Same(OwnRules.Empty, WaveshareBoard.ToBoardState(new WaveshareState(true, new WaveshareStatus(0, 0, new ushort[8]), at)).OwnRules);

        // Not known: no rules at all, never a guess.
        Assert.Null(WaveshareBoard.ToBoardState(new WaveshareState(true, new WaveshareStatus(0, 0), at)).OwnRules);
        Assert.Equal(ChannelMask.None, WaveshareBoard.ToBoardState(new WaveshareState(true, new WaveshareStatus(0, 0), at)).Unavailable);
    }

    [Fact]
    public async Task Should_TurnOffOnlyTheModesAsked_AndReturnTheRulesTheBoardTook()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeLinkage);
        await simulator.SetRelayModeAsync(3, WaveshareApi.ModeEdge);
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "127.0.0.1", Port = simulator.Port }),
            WaveshareModels.Relay8B,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var board = new WaveshareBoard(client, NullLogger<WaveshareBoard>.Instance);
        List<BoardState> ownRules = [];
        board.OwnRulesChanged += (_, s) => ownRules.Add(s);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOffOwnRulesAsync([new OwnRule(2, OwnRuleKind.Timer)], Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOffOwnRulesAsync([new OwnRule(9, OwnRuleKind.InputEdge)], Cancel));
        Assert.Empty(await board.TurnOffOwnRulesAsync([], Cancel));
        var taken = await board.TurnOffOwnRulesAsync([new OwnRule(2, OwnRuleKind.InputLinkage), new OwnRule(2, OwnRuleKind.InputLinkage)], Cancel);

        Assert.Equal([new OwnRule(2, OwnRuleKind.InputLinkage)], taken);
        Assert.Equal(new ushort[] { 0, 0, 3, 0, 0, 0, 0, 0 }, simulator.RelayModes);
        Assert.Equal(new OwnRules([new(3, OwnRuleKind.InputEdge)]), board.State.OwnRules);
        Assert.Equal(new OwnRules([new(3, OwnRuleKind.InputEdge)]), Assert.Single(ownRules).OwnRules);
        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public async Task Should_SayTheModelsWithoutModes_CantTurnOwnRulesOff()
    {
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "127.0.0.1" }),
            WaveshareModels.Relay8,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var board = new WaveshareBoard(client, NullLogger<WaveshareBoard>.Instance);

        await Assert.ThrowsAsync<NotSupportedException>(() => board.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.InputLinkage)], Cancel));
        Assert.Null(board.State.OwnRules);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheModelHasNot_BeforeAnythingIsSent()
    {
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "127.0.0.1" }),
            WaveshareModels.Relay16,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var board = new WaveshareBoard(client, NullLogger<WaveshareBoard>.Instance);

        Assert.Equal(16, board.Capabilities.RelayCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(17), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(16), Cancel));
    }

    [Fact]
    public async Task Should_RefuseAPulse_ItCantDo_BeforeAnythingIsSent()
    {
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "127.0.0.1" }),
            WaveshareModels.Relay16,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var board = new WaveshareBoard(client, NullLogger<WaveshareBoard>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromMilliseconds(3_276_800), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromMilliseconds(250), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(17), TimeSpan.FromSeconds(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.PulseAsync(ChannelMask.Of(16), TimeSpan.FromSeconds(1), Cancel));
    }

    [Fact]
    public async Task Should_Pulse_AndReturnOnlyTheRelaysTheBoardTook()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = boardTime, FailRelays = 0b0100 };
        simulator.Start();
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "127.0.0.1", Port = simulator.Port }),
            WaveshareModels.Relay8,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var board = new WaveshareBoard(client, NullLogger<WaveshareBoard>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.PulseAsync(ChannelMask.Of([2, 3, 4]), TimeSpan.FromSeconds(2.5), Cancel));

        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);
        Assert.Contains("01 05 02 01 00 19 5C 78", simulator.ReceivedRequests);
        boardTime.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(0UL, simulator.Relays);
        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public void Should_TakeNoLogin()
    {
        using var client = new WaveshareClient(
            Options.Create(new WaveshareOptions { Host = "", Port = 4196 }),
            WaveshareModels.Relay8,
            new ManualTimeProvider(),
            NullLogger<WaveshareClient>.Instance);
        var connection = new WaveshareConnection(client, Options.Create(new WaveshareOptions()));

        Assert.Null(connection.CheckLogin(BoardLogin.None));
        Assert.NotNull(connection.CheckLogin(new BoardLogin("admin", "123456")));
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "")));
        Assert.Equal(new BoardEndpoint("", 4196), connection.DefaultEndpoint);
    }
}
