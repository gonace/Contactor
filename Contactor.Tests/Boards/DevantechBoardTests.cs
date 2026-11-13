using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The Devantech boards as the app sees them: the four models, their state, their relays and the password.</summary>
public sealed class DevantechBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("devantech-eth002", "Devantech ETH002-B", 2, 0, 0, 18)]
    [InlineData("devantech-eth008", "Devantech ETH008-B", 8, 0, 0, 19)]
    [InlineData("devantech-eth484", "Devantech ETH484-B", 4, 8, 4, 20)]
    [InlineData("devantech-eth8020", "Devantech ETH8020-B", 20, 8, 8, 21)]
    public void Should_DescribeEachModel(string id, string name, int relays, int inputs, int analog, byte moduleId)
    {
        var model = Assert.Single(DevantechModels.All, m => m.Id == id);

        Assert.Equal(name, model.Name);
        var pulse = new PulseAbility(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(25_500), TimeSpan.FromMilliseconds(100));
        Assert.Equal(new BoardCapabilities(relays, inputs, ReportsChanges: false, AnalogInputCount: analog, Pulse: pulse), model.Capabilities);
        Assert.Equal(17494, model.DefaultPort);
        Assert.True(model.HasLogin);
        Assert.True(model.PasswordOnly);
        Assert.Equal(32, model.LoginMaxLength);
        Assert.Equal(moduleId, DevantechModels.Layout(model).ModuleId);
    }

    [Fact]
    public void Should_TranslateTheState()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        var state = DevantechBoard.ToBoardState(new DevantechState(true, new DevantechStatus(1UL << 19, 0b1000_0001), at));

        Assert.Equal(new BoardState(true, ChannelMask.Of(20), ChannelMask.Of([1, 8]), at), state);
        Assert.Equal(BoardState.Initial, DevantechBoard.ToBoardState(DevantechState.Initial));
    }

    [Fact]
    public async Task Should_RefuseRelaysTheModelHasNot_BeforeAnythingIsSent()
    {
        using var client = Client(new DevantechOptions { Host = "127.0.0.1" }, DevantechModels.Eth484);
        var board = new DevantechBoard(client, NullLogger<DevantechBoard>.Instance);

        Assert.Equal(4, board.Capabilities.RelayCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(5), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(4), Cancel));
    }

    [Fact]
    public async Task Should_RefuseAPulse_ItCantDo_BeforeAnythingIsSent()
    {
        using var client = Client(new DevantechOptions { Host = "127.0.0.1" }, DevantechModels.Eth484);
        var board = new DevantechBoard(client, NullLogger<DevantechBoard>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(26), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromMilliseconds(50), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.PulseAsync(ChannelMask.Of(5), TimeSpan.FromSeconds(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.PulseAsync(ChannelMask.Of(4), TimeSpan.FromSeconds(1), Cancel));
    }

    [Fact]
    public async Task Should_Pulse_AndReturnOnlyTheRelaysTheBoardTook()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = new DevantechSimulator(DevantechModels.Eth008, null, NullLogger.Instance) { TimeProvider = boardTime, FailRelays = 0b0100 };
        simulator.Start();
        using var client = Client(new DevantechOptions { Host = "127.0.0.1", Port = simulator.Port }, DevantechModels.Eth008);
        var board = new DevantechBoard(client, NullLogger<DevantechBoard>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.PulseAsync(ChannelMask.Of([2, 3, 4]), TimeSpan.FromSeconds(2.5), Cancel));

        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);
        Assert.Contains("20 02 19", simulator.ReceivedRequests);
        boardTime.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(0UL, simulator.Relays);
        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public void Should_TakeAPassword_ButNoUserName()
    {
        var options = new DevantechOptions { Host = "", Password = "apple" };
        using var client = Client(options, DevantechModels.Eth008);
        var connection = new DevantechConnection(client, Options.Create(options));

        Assert.Equal(new BoardEndpoint("", 17494), connection.DefaultEndpoint);
        Assert.Equal(new BoardLogin("", "apple"), connection.DefaultLogin);
        Assert.Null(connection.CheckLogin(BoardLogin.None));
        Assert.Null(connection.CheckLogin(new BoardLogin("", "pear")));
        Assert.Equal("The Devantech board takes a password only, no user name.", connection.CheckLogin(new BoardLogin("admin", "pear")));
        Assert.Equal("The password can be at most 32 characters.", connection.CheckLogin(new BoardLogin("", new string('a', 33))));
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "")));

        connection.SetLogin(new BoardLogin("", "pear"));
        Assert.Equal(new BoardLogin("", "pear"), connection.Login);
        connection.SetLogin(BoardLogin.None);
        Assert.True(connection.Login.IsNone);
    }

    private static DevantechClient Client(DevantechOptions options, BoardModel model)
    {
        return new DevantechClient(Options.Create(options), model, new ManualTimeProvider(), NullLogger<DevantechClient>.Instance);
    }
}
