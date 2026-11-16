using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Advantech;

/// <summary>The ADAM modules as the app sees them: their models, their state, their relays and their login.</summary>
public sealed class AdamBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("advantech-adam-6060", "ADAM-6060", 6)]
    [InlineData("advantech-adam-6066", "ADAM-6066", 6)]
    [InlineData("advantech-adam-6260", "ADAM-6260", 0)]
    public void Should_DescribeTheModels(string id, string name, int inputs)
    {
        var model = Assert.Single(AdamModels.All, m => m.Id == id);

        Assert.Equal("Advantech", model.Brand);
        Assert.Equal(name, model.ModelName);
        Assert.Equal(6, model.Capabilities.RelayCount);
        Assert.Equal(inputs, model.Capabilities.InputCount);
        Assert.False(model.Capabilities.ReportsChanges);
        Assert.Null(model.Capabilities.Pulse);
        Assert.Equal(502, model.DefaultPort);
        Assert.Equal(0, model.LoginMaxLength);
        Assert.True(AdamModels.IsAdam(model));
        Assert.False(AdamModels.IsAdam(Contactor.Boards.Velleman.Vm201Model.Model));
    }

    [Fact]
    public void Should_ShowTheStateAsABoardState()
    {
        var at = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

        var state = AdamBoard.ToBoardState(new AdamState(true, new AdamStatus((1UL << 5) | 1UL, 0b10), at));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 6]), ChannelMask.Of(2), at), state);
        Assert.Equal(BoardState.Initial, AdamBoard.ToBoardState(AdamState.Initial));
    }

    [Fact]
    public async Task Should_RefuseRelaysItHasNot_AndAnyPulse_BeforeAnythingIsSent()
    {
        using var client = CreateClient(1);
        var board = new AdamBoard(client, NullLogger<AdamBoard>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(7), Cancel));
        await Assert.ThrowsAsync<NotSupportedException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(6), Cancel));
    }

    [Fact]
    public async Task Should_ReturnOnlyTheRelaysTheModuleTook_AndNeverLetARefusalOut()
    {
        await using var simulator = new AdamSimulator(AdamModels.Adam6066) { FailRelays = 0b0100 };
        simulator.Start();
        using var client = CreateClient(simulator.Port);
        var board = new AdamBoard(client, NullLogger<AdamBoard>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.TurnOnAsync(ChannelMask.Of([2, 3, 4]), Cancel));
        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);
        Assert.Contains(simulator.ReceivedRequests, r => r.EndsWith("01 05 00 12 FF 00", StringComparison.Ordinal));

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
        using var client = CreateClient(AdamApi.DefaultPort, host: "");
        var connection = new AdamConnection(client, Options.Create(new AdamOptions()));

        Assert.Null(connection.CheckLogin(BoardLogin.None));
        var problem = connection.CheckLogin(new BoardLogin("root", "00000000"));
        Assert.Equal(new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: "Advantech"), problem);
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("root", "")));
        Assert.Equal(BoardLogin.None, connection.Login);
        Assert.Equal(new BoardEndpoint("", 502), connection.DefaultEndpoint);
    }

    private static AdamClient CreateClient(int port, string host = "127.0.0.1")
    {
        return new AdamClient(
            Options.Create(new AdamOptions { Host = host, Port = port }),
            AdamModels.Adam6060,
            new ManualTimeProvider(),
            NullLogger<AdamClient>.Instance);
    }
}
