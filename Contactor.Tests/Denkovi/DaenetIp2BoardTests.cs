using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Denkovi;

/// <summary>The DAEnetIP2 v2 kits as the app sees them: their models, pins, state, relays and login.</summary>
public sealed class DaenetIp2BoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("denkovi-daenetip2-v2-16", "DAEnetIP2 v2 16-relay kit", 16)]
    [InlineData("denkovi-daenetip2-v2-8", "DAEnetIP2 v2 8-relay kit", 8)]
    [InlineData("denkovi-daenetip2-v2-2", "DAEnetIP2 v2 2-relay kit", 2)]
    public void Should_DescribeEachKit(string id, string name, int relays)
    {
        var model = Assert.Single(DaenetIp2Models.All, m => m.Id == id);

        Assert.Equal(("Denkovi", name, $"Denkovi {name}"), (model.Brand, model.ModelName, model.Name));
        Assert.Equal(new BoardCapabilities(relays, 0, ReportsChanges: false), model.Capabilities);
        Assert.Null(model.Capabilities.Pulse);
        Assert.Equal(161, model.DefaultPort);
        Assert.Equal(12, model.LoginMaxLength);
        Assert.True(model.PasswordOnly);
        Assert.Equal(new LoginHint(LoginHintKind.SnmpCommunity, 12, MinLength: 4, FactoryValue: "private"), model.LoginHint);
        Assert.True(model.LoginRequired);
        Assert.True(DaenetIp2Models.IsDaenetIp2(model));
        Assert.Equal(relays, DaenetIp2Models.Layout(model).Relays.Count);
    }

    [Fact]
    public void Should_PutEachKitsRelays_OnThePinsTheManualSays()
    {
        var relays16 = DaenetIp2Models.Layout(DaenetIp2Models.Relays16);
        var relays8 = DaenetIp2Models.Layout(DaenetIp2Models.Relays8);
        var relays2 = DaenetIp2Models.Layout(DaenetIp2Models.Relays2);

        Assert.Equal(["P3.1", "P3.8", "P5.1", "P5.8"], Pins(relays16, 1, 8, 9, 16));
        Assert.Equal([DaenetIp2Port.P3, DaenetIp2Port.P5], relays16.Ports);
        Assert.Equal(["P5.1", "P5.8"], Pins(relays8, 1, 8));
        Assert.Equal([DaenetIp2Port.P5], relays8.Ports);
        Assert.Equal(["P5.7", "P5.8"], Pins(relays2, 1, 2));
        Assert.Equal([DaenetIp2Port.P5], relays2.Ports);
        Assert.Null(relays2.RelayOf(new DaenetIp2Pin(DaenetIp2Port.P5, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => relays2.PinOf(3));
        Assert.Throws<ArgumentException>(() => DaenetIp2Models.Layout(Vm201Model.Model));
    }

    [Theory]
    [InlineData("denkovi-daenetip2-v2-16", 0b0000_0001, 0b1000_0000, (1UL << 15) | 1UL)]
    [InlineData("denkovi-daenetip2-v2-16", 0xFF, 0xFF, 0xFFFFUL)]
    [InlineData("denkovi-daenetip2-v2-8", 0xFF, 0b0000_0101, 0b0101UL)]
    [InlineData("denkovi-daenetip2-v2-2", 0xFF, 0b0111_1111, 0b01UL)]
    [InlineData("denkovi-daenetip2-v2-2", 0x00, 0b1000_0000, 0b10UL)]
    public void Should_ReadARelayAsOn_WhenItsPinIsHigh(string id, int p3, int p5, ulong relays)
    {
        var model = DaenetIp2Models.All.Single(m => m.Id == id);

        Assert.Equal(relays, DaenetIp2Models.Layout(model).RelaysOf(p3, p5));
    }

    [Fact]
    public void Should_RefuseALayoutThatIsntOne()
    {
        Assert.Throws<ArgumentException>(() => new DaenetIp2Layout([]));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Layout([new(DaenetIp2Port.P5, 1), new(DaenetIp2Port.P5, 1)]));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Layout([new(DaenetIp2Port.P5, 9)]));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Layout([new((DaenetIp2Port)7, 1)]));
    }

    [Fact]
    public void Should_ShowTheStateAsABoardState()
    {
        var at = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

        var state = DaenetIp2Board.ToBoardState(new DaenetIp2State(true, new DaenetIp2Status((1UL << 15) | 1UL), at));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 16]), ChannelMask.None, at), state);
        Assert.Equal(BoardState.Initial, DaenetIp2Board.ToBoardState(DaenetIp2State.Initial));
    }

    [Fact]
    public async Task Should_RefuseRelaysItHasNot_AndPulses_BeforeAnythingIsSent()
    {
        using var client = CreateClient(DaenetIp2Models.Relays2);
        var board = new DaenetIp2Board(client, NullLogger<DaenetIp2Board>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(3), Cancel));
        await Assert.ThrowsAsync<NotSupportedException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(2), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(2), Cancel));
        Assert.Equal(2, board.Capabilities.RelayCount);
    }

    [Fact]
    public async Task Should_ReturnOnlyTheRelaysTheBoardTook_AndNeverLetARefusalOut()
    {
        await using var simulator = new DaenetIp2Simulator(DaenetIp2Models.Relays8) { FailRelays = 0b0100 };
        simulator.Start();
        using var client = CreateClient(DaenetIp2Models.Relays8, simulator.Port, community: DaenetIp2Api.FactoryReadWriteCommunity);
        var board = new DaenetIp2Board(client, NullLogger<DaenetIp2Board>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 4]), await board.TurnOnAsync(ChannelMask.Of([2, 3, 4]), Cancel));
        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);

        // Every relay refused: "didn't take the command", as every board says it, never the poller's refusal.
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(3), Cancel));
        Assert.IsNotType<BoardRefusedException>(error);
        Assert.True(board.State.IsConnected);

        await stop.CancelAsync();
        await run.WaitAsync(Cancel);
    }

    [Fact]
    public void Should_TakeTheReadWriteCommunity_AsAPasswordAlone()
    {
        using var client = CreateClient(DaenetIp2Models.Relays16, community: "private");
        var connection = new DaenetIp2Connection(client, Options.Create(new DaenetIp2Options { Community = "private" }));

        Assert.Equal(new BoardLogin("", "private"), connection.DefaultLogin);
        Assert.Equal(new BoardLogin("", "private"), connection.Login);
        var length = new LoginProblem(LoginProblemKind.CommunityLength, MaxLength: 12, MinLength: 4);
        Assert.Null(connection.CheckLogin(new BoardLogin("", "123456789012")));
        Assert.Null(connection.CheckLogin(new BoardLogin("", "1234")));
        var missing = new LoginProblem(LoginProblemKind.CommunityMissing);
        Assert.Equal(missing, connection.CheckLogin(BoardLogin.None));
        Assert.Equal("Type the password, the board's read-write community.", missing.ToString());
        Assert.Equal(length, connection.CheckLogin(new BoardLogin("", "123")));
        Assert.Equal(length, connection.CheckLogin(new BoardLogin("", "1234567890123")));
        Assert.Equal("The password, the board's read-write community, must be 4 to 12 characters.", length.ToString());
        Assert.Equal(new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: "Denkovi"), connection.CheckLogin(new BoardLogin("admin", "private")));
        Assert.Equal(new LoginProblem(LoginProblemKind.PasswordCharacters), connection.CheckLogin(new BoardLogin("", "pri vate")));
        Assert.Equal(new LoginProblem(LoginProblemKind.PasswordCharacters), connection.CheckLogin(new BoardLogin("", "privåte")));
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "")));

        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("", "abc")));
        connection.SetLogin(new BoardLogin("", "apple"));
        Assert.Equal(new BoardLogin("", "apple"), connection.Login);
        Assert.Equal("apple", client.Community);

        // None is still taken, as the app falls back to it: no community yet, which the board doesn't answer.
        connection.SetLogin(BoardLogin.None);
        Assert.Equal(BoardLogin.None, connection.Login);
        Assert.Equal(new BoardEndpoint("", 161), connection.DefaultEndpoint);
    }

    private static List<string> Pins(DaenetIp2Layout layout, params int[] relays)
    {
        return [.. relays.Select(r => layout.PinOf(r).ToString())];
    }

    private static DaenetIp2Client CreateClient(BoardModel model, int port = 161, string community = "")
    {
        return new DaenetIp2Client(
            Options.Create(new DaenetIp2Options { Host = "127.0.0.1", Port = port, Community = community }),
            model,
            new ManualTimeProvider(),
            NullLogger<DaenetIp2Client>.Instance);
    }
}
