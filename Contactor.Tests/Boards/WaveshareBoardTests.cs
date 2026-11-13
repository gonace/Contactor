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
        Assert.Equal(new BoardCapabilities(relays, inputs, ReportsChanges: false), model.Capabilities);
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
