using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
using Contactor.Boards.Waveshare;
using Contactor.Simulator;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Simulator;

/// <summary>The boards running in the simulator, as the page adds, drives and removes them.</summary>
public sealed class SimulatedBoardsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);

    private readonly SimulatedBoards _boards = new(NullLoggerFactory.Instance, new FixedTimeProvider(Now));

    public async ValueTask DisposeAsync()
    {
        await _boards.DisposeAsync();
    }

    [Fact]
    public void Should_RunSeveralBoards_EachOnItsOwnPort()
    {
        var (first, second) = (FreePort(), FreePort());

        Assert.Null(_boards.Add("vm201", second, null, null));
        Assert.Null(_boards.Add(" VM201 ", first, "john", "secret"));

        Assert.Equal(new[] { first, second }.Order(), _boards.Boards.Select(b => b.Board.Port));
        var withLogin = _boards.Boards.Single(b => b.Board.Port == first);
        Assert.Equal("john", withLogin.Username);
        Assert.True(withLogin.Board.HasLogin);
        Assert.Same(Vm201Model.Model, withLogin.Board.Model);
        Assert.False(withLogin.FromSettings);
    }

    [Fact]
    public void Should_RunAVm204_NextToAVm201()
    {
        Assert.Null(_boards.Add("vm201", FreePort(), null, null));
        Assert.Null(_boards.Add("vm204", FreePort(), "admin", "VM204"));

        var vm204 = _boards.Boards.Single(b => b.Board.Model == Vm204Model.Model);
        Assert.True(vm204.Board.HasLogin);
        Assert.Equal("admin", vm204.Username);

        // Port 80 needs rights a developer doesn't have; the page offers a free high port instead.
        Assert.Equal((8204, 80), (SimulatorCatalogue.Find("vm204")!.Port, Vm204Model.Model.DefaultPort));
        Assert.Equal(9760, SimulatorCatalogue.Find("vm201")!.Port);
    }

    [Fact]
    public void Should_RunAVm208_WithoutALogin()
    {
        Assert.Null(_boards.Add("vm208", FreePort(), "", ""));

        var vm208 = _boards.Boards.Single(b => b.Board.Model == Vm208Model.Model);
        Assert.False(vm208.Board.HasLogin);
        Assert.Null(vm208.Username);

        // Port 80 needs rights a developer doesn't have; the page offers a free high port instead.
        Assert.Equal((8208, 80), (SimulatorCatalogue.Find("vm208")!.Port, Vm208Model.Model.DefaultPort));
    }

    [Theory]
    [InlineData("waveshare-8")]
    [InlineData("waveshare-8b")]
    [InlineData("waveshare-8c")]
    [InlineData("waveshare-16")]
    [InlineData("WAVESHARE-30 ")]
    public void Should_RunEachWaveshareModel_WithoutALogin(string type)
    {
        Assert.Null(_boards.Add(type, FreePort(), null, null));

        var board = Assert.Single(_boards.Boards).Board;
        Assert.IsType<WaveshareSimulator>(board);
        Assert.Equal(type.Trim().ToLowerInvariant(), board.Model.Id);
        Assert.False(board.HasLogin);
        Assert.Equal(4196, SimulatorCatalogue.Find(type)!.Port);
        Assert.Equal("The Waveshare board takes no login.", _boards.Add(type, FreePort(), "admin", "secret"));
    }

    [Theory]
    [InlineData("vm209", 9760, null, null, "Choose a model.")]
    [InlineData("vm208", 9760, "admin", "secret", "The VM208 takes no login.")]
    [InlineData("", 9760, null, null, "Choose a model.")]
    [InlineData("vm201", 0, null, null, "The port is a number from 1 to 65535.")]
    [InlineData("vm201", 65536, null, null, "The port is a number from 1 to 65535.")]
    [InlineData("vm201", 9760, "", "secret", "Write the user name too, or leave both empty.")]
    [InlineData("vm201", 9760, "johnathan", "secret", "The user name can be at most 8 characters on the relay board.")]
    [InlineData("vm204", 9760, "admin", "a&b", "The password can only contain letters a–z, digits and - . _ ~ ! $ ' ( ) * + , ; : @ on the relay board.")]
    public void Should_SaySo_ForABoardThatCannotRun(string type, int port, string? username, string? password, string message)
    {
        Assert.Equal(message, _boards.Add(type, port, username, password));
        Assert.Empty(_boards.Boards);
    }

    [Fact]
    public void Should_TakeALogin_WithAColonInTheUserNameAndThePassword()
    {
        // A VM204 user name may hold ':', so the page asks for the user name and the password apart.
        Assert.Null(_boards.Add("vm204", FreePort(), "ad:min", "p:w"));
        Assert.Null(_boards.Add("vm201", FreePort(), "john", ""));

        var vm204 = _boards.Boards.Single(b => b.Board.Model == Vm204Model.Model);
        Assert.Equal("ad:min", vm204.Username);
        Assert.True(vm204.Board.HasLogin);
        Assert.Equal("john", _boards.Boards.Single(b => b.Board.Model == Vm201Model.Model).Username);
    }

    [Fact]
    public void Should_SaySo_WhenThePortIsTaken()
    {
        var port = FreePort();
        Assert.Null(_boards.Add("vm201", port, null, null));
        Assert.Equal($"Port {port} is already used by another board here.", _boards.Add("vm201", port, null, null));

        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var taken = ((IPEndPoint)other.LocalEndpoint).Port;
        Assert.Equal($"Port {taken} is already in use on this computer.", _boards.Add("vm201", taken, null, null));
        Assert.Single(_boards.Boards);
    }

    [Fact]
    public async Task Should_FreeThePort_WhenABoardIsRemoved()
    {
        var port = FreePort();
        Assert.Null(_boards.Add("vm201", port, null, null));
        var changes = 0;
        _boards.Changed += (_, _) => changes++;

        await _boards.RemoveAsync(_boards.Boards.Single().Id);
        await _boards.RemoveAsync(12345);

        Assert.Empty(_boards.Boards);
        Assert.Equal(1, changes);
        Assert.Null(_boards.Add("vm201", port, null, null));
    }

    [Fact]
    public async Task Should_StartNothing_OnceStopped()
    {
        await _boards.DisposeAsync();

        Assert.Equal("The simulator is stopping.", _boards.Add("vm201", FreePort(), null, null));
    }

    [Fact]
    public async Task Should_LogWhatIsDoneAtTheBoard_AndLeaveTheKeepAliveOut()
    {
        Assert.Null(_boards.Add("vm201", FreePort(), null, null));
        var board = _boards.Boards.Single();
        await using var client = await ConnectAsync(board.Board.Port);

        await board.SwitchRelayAsync(3, turnOn: true);
        await board.SetInputAsync(1, closed: true);
        await board.SetTimersAsync(on: true);
        await WaitUntilAsync(() => board.Traffic.Any(t => t.Text == "Status: relay 3 on · the board's timers on for all · input on"));
        await client.Client.RequestStatusAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => board.HiddenRoutine >= 2);

        var traffic = board.Traffic;
        Assert.Contains(traffic, t => t is { Kind: TrafficKind.AtBoard, Text: "Relay 3 switched on here" });
        Assert.Contains(traffic, t => t is { Kind: TrafficKind.AtBoard, Text: "Input 1 closed here" });
        Assert.Contains(traffic, t => t is { Kind: TrafficKind.AtBoard, Text: "The board's own timers turned on here" });
        Assert.Contains(traffic, t => t.Kind == TrafficKind.Connection && t.Text.StartsWith("The app connected from 127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(traffic, t => t is { Kind: TrafficKind.BoardToApp, Text: "Logged in" });
        Assert.DoesNotContain(traffic, t => t.Text == "Ask for status");
        Assert.True(traffic.Zip(traffic.Skip(1)).All(p => p.First.Id > p.Second.Id), "Newest first.");
        Assert.Equal(ChannelMask.Of(3), board.Board.State.Relays);
        Assert.Equal(board.Board.Model.Capabilities.AllRelays, ((ISimulatedBoardTimers)board.Board).Timers);

        board.ClearTraffic();
        Assert.Empty(board.Traffic);
        Assert.Equal(0, board.HiddenRoutine);
    }

    [Fact]
    public async Task Should_KeepTheLatestHundredLines()
    {
        Assert.Null(_boards.Add("vm201", FreePort(), null, null));
        var board = _boards.Boards.Single();

        for (var i = 0; i < RunningBoard.Capacity + 20; i++)
        {
            await board.SwitchRelayAsync(1, turnOn: i % 2 == 0);
        }

        Assert.Equal(RunningBoard.Capacity, board.Traffic.Count);
        Assert.Equal("Relay 1 switched off here", board.Traffic[0].Text);
    }

    [Fact]
    public async Task Should_IgnoreAButtonPressed_AfterTheBoardStopped()
    {
        Assert.Null(_boards.Add("vm201", FreePort(), null, null));
        var board = _boards.Boards.Single();
        await _boards.RemoveAsync(board.Id);

        await board.SwitchRelayAsync(1, turnOn: true);
        await board.SetInputAsync(1, closed: true);
        await board.SetTimersAsync(on: false);
    }

    [Fact]
    public async Task Should_StartTheBoardsFromTheSettings()
    {
        var port = FreePort();
        var options = new SimulatorOptions { Boards = [new SimulatorBoardOptions { Type = "vm201", Port = port, Username = "john", Password = "secret" }] };
        var service = new SimulatorHostedService(_boards, Options.Create(options));

        await service.StartAsync(TestContext.Current.CancellationToken);

        var board = Assert.Single(_boards.Boards);
        Assert.Equal((port, "john", true), (board.Board.Port, board.Username, board.FromSettings));
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_boards.Boards);
    }

    [Fact]
    public async Task Should_KeepAColonInTheSettingsUserName()
    {
        var options = new SimulatorOptions { Boards = [new SimulatorBoardOptions { Type = "vm201", Port = FreePort(), Username = "ab:c", Password = "x" }] };
        var service = new SimulatorHostedService(_boards, Options.Create(options));

        await service.StartAsync(TestContext.Current.CancellationToken);

        var board = Assert.Single(_boards.Boards);
        Assert.Equal("ab:c", board.Username);
        await using var client = await ConnectAsync(board.Board.Port, "ab:c", "x");
        Assert.True(client.Client.State.IsConnected);
    }

    [Fact]
    public void Should_TakeAnEmptyPassword_FromThePage()
    {
        Assert.Null(_boards.Add("vm201", FreePort(), "john", ""));

        Assert.Equal("john", Assert.Single(_boards.Boards).Username);
    }

    [Fact]
    public async Task Should_RefuseToStart_WhenABoardInTheSettingsCannotRun()
    {
        var options = new SimulatorOptions { Boards = [new SimulatorBoardOptions { Type = "vm209", Port = FreePort() }] };
        var service = new SimulatorHostedService(_boards, Options.Create(options));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.EndsWith("Choose a model.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_StartOneVm201OnItsOwnPort_WhenTheSettingsNameNone()
    {
        var board = Assert.Single(SimulatorHostedService.DefaultBoards);

        Assert.Equal(("vm201", Vm201Model.DefaultPort, "", ""), (board.Type, board.Port, board.Username, board.Password));
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<ConnectedClient> ConnectAsync(int port, string username = "", string password = "")
    {
        var options = new Vm201Options { Host = "127.0.0.1", Port = port, Username = username, Password = password };
        var client = new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
        var stop = new CancellationTokenSource();
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => client.State.IsConnected);
        return new ConnectedClient(client, stop, run);
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

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed record ConnectedClient(Vm201Client Client, CancellationTokenSource Stop, Task Run) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            await Run;
            Stop.Dispose();
        }
    }
}
