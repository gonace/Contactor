using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Simulator;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Simulator;

/// <summary>A simulated board that answers slowly, and a running board's new login, as the page sets them.</summary>
public sealed class SimulatedSlowAndLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The VM201 answers its login late, once, and holds nothing meanwhile: the board still takes a change.
    [Fact]
    public async Task Should_AnswerTheVm201sLoginLate_WithoutHoldingTheBoard()
    {
        var time = new ManualTimeProvider();
        await using var vm201 = new Vm201Simulator { TimeProvider = time };
        vm201.Start();
        vm201.AnswerDelay = TimeSpan.FromSeconds(4);
        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, vm201.Port, Cancel);

        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(4)));
        await vm201.SwitchRelayAsync(2, true);
        Assert.Equal(ChannelMask.Of(2), vm201.State.Relays);
        Assert.Equal(0, app.Available);

        time.Advance(TimeSpan.FromSeconds(4));
        var first = new byte[3];
        await app.GetStream().ReadExactlyAsync(first, Cancel);
        Assert.Equal(Vm201Command.LoggedIn, first[2]);
    }

    // One slow DAEnetIP2 answer holds up no other datagram; an answer due after the power went is lost.
    [Fact]
    public async Task Should_AnswerOtherDatagrams_DuringASlowDaenetIp2Answer_AndLoseOneWhenThePowerGoes()
    {
        var time = new ManualTimeProvider();
        await using var daenet = new DaenetIp2Simulator(DaenetIp2Models.Relays8, null, NullLogger.Instance) { TimeProvider = time };
        daenet.Start();
        using var app = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        app.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var community = DaenetIp2Api.FactoryReadWriteCommunity;

        daenet.AnswerDelay = TimeSpan.FromSeconds(4);
        await SendAsync(app, daenet, DaenetIp2Snmp.GetPort(1, community, DaenetIp2Port.P5).Message);
        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(4)));
        daenet.AnswerDelay = TimeSpan.Zero;
        Assert.Equal(2, await AskAsync(app, daenet, DaenetIp2Snmp.GetPort(2, community, DaenetIp2Port.P5).Message));

        await daenet.CutPowerAsync();
        time.Advance(TimeSpan.FromSeconds(4));
        await Wait.UntilAsync(() => daenet.UnansweredCount == 1);
        await daenet.RestorePowerAsync();

        // Datagrams on loopback keep their order: had request 1 been answered, its answer would come first.
        Assert.Equal(3, await AskAsync(app, daenet, DaenetIp2Snmp.GetPort(3, community, DaenetIp2Port.P5).Message));
    }

    [Fact]
    public async Task Should_ChangeTheShellysPassword_ThroughTheCommonLogin()
    {
        await using var shelly = new ShellySimulator(ShellyModels.OneRelay, "apple", NullLogger.Instance);
        var login = (ISimulatedBoardLogin)shelly;

        Assert.Throws<ArgumentException>(() => login.ChangeLogin("", new string('a', ShellyApi.PasswordMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => login.ChangeLogin("", ""));
        login.ChangeLogin("ignored", "pear");

        await using var open = new ShellySimulator(ShellyModels.OneRelay);
        Assert.Throws<InvalidOperationException>(() => ((ISimulatedBoardLogin)open).ChangeLogin("", "pear"));
    }

    // The page's new login is checked as when the board was added, and turned down in the board's own words.
    [Theory]
    [InlineData("vm201", "admin", "secret", "johnjohnj", "x", "The user name can be at most 8 characters on the relay board.")]
    [InlineData("vm201", "admin", "secret", "", "x", "Write the new user name and password.")]
    [InlineData("devantech-eth008", "", "apple", "", "pe ar", "The password can only contain")]
    [InlineData("denkovi-daenetip2-v2-8", "", "private", "", "abc", "4 to 12")]
    public async Task Should_TurnDownALoginTheBoardDoesntTake(string type, string username, string password, string newUsername, string newPassword, string error)
    {
        var model = SimulatorCatalogue.Find(type)!;
        await using var board = new RunningBoard(1, model.Create(username, password, NullLogger.Instance, TimeProvider.System), username, fromSettings: false, new FixedTimeProvider(Now));

        var refused = board.ChangeLogin(newUsername, newPassword);

        Assert.NotNull(refused);
        Assert.Contains(error, refused, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", refused, StringComparison.Ordinal);
        Assert.Equal(username, board.Username);
    }

    [Fact]
    public async Task Should_ChangeTheLogin_AndSayItWithoutThePassword()
    {
        await using var board = new RunningBoard(1, new Vm201Simulator("admin", "secret", NullLogger.Instance), "admin", fromSettings: false, new FixedTimeProvider(Now));

        Assert.Null(board.ChangeLogin(" root", "newpw"));

        Assert.Equal(" root", board.Username);
        Assert.Equal("Login changed here to user  root: the app's no longer fits", board.Traffic[0].Text);
        Assert.DoesNotContain(board.Traffic, t => t.Text.Contains("newpw", StringComparison.Ordinal));

        await using var none = new RunningBoard(2, new Vm201Simulator(), null, fromSettings: false, new FixedTimeProvider(Now));
        Assert.Equal("The board has no login to change.", none.ChangeLogin("a", "b"));
    }

    private static async Task SendAsync(Socket app, DaenetIp2Simulator board, byte[] datagram)
    {
        await app.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, board.Port), Cancel);
    }

    // Sends a request and returns the request id of the next answer.
    private static async Task<int> AskAsync(Socket app, DaenetIp2Simulator board, byte[] datagram)
    {
        await SendAsync(app, board, datagram);
        var buffer = new byte[DaenetIp2Api.MaxMessageBytes + 1];
        var received = await app.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), Cancel);
        return DaenetIp2Snmp.Decode(buffer.AsSpan(0, received.ReceivedBytes).ToArray()).RequestId;
    }
}
