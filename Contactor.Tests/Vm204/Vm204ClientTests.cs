using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Vm204;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm204;

/// <summary>The client against the simulator, and against a fake web server for answers the simulator never gives.</summary>
public sealed class Vm204ClientTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogInAndReportStatus_WhenTheBoardsLoginIsEmpty()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SetInputAsync(4, true);
        simulator.SetAnalog(512);
        await using var run = ClientRun.Start(simulator.Port);

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new Vm204Status(0b0010, 0b1000, 512), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(2));
        Assert.NotNull(run.Client.State.LastContactUtc);
        Assert.Equal($"{Vm204Api.AuthPath}?user=&password=", simulator.ReceivedTargets[0]);
    }

    [Fact]
    public async Task Should_LogIn_WithEveryCharacterTheBoardTakes()
    {
        // The firmware compares the query as it arrives: each symbol must reach it unchanged.
        const string username = "Admin-._~";
        const string password = "p!$'()*+,;:@9";
        await using var simulator = new Vm204Simulator(username, password, NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, username, password);

        await WaitUntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0, simulator.DeniedLoginCount);
        Assert.Contains($"{Vm204Api.AuthPath}?user={username}&password={password}", simulator.ReceivedTargets);
    }

    [Fact]
    public async Task Should_StayDisconnectedAndRetry_WhenThePasswordIsWrong()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "vm204");

        await WaitUntilAsync(() => simulator.DeniedLoginCount >= 2);

        Assert.False(run.Client.State.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
        Assert.Contains(run.Traces, t => t.Text == "Wrong user name or password" && t.IsError);
        Assert.All(run.Traces, t => Assert.DoesNotContain("vm204", $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SwitchRelays_AndPollAtOnceAfterwards()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();

        // A poll a minute away: the change can only show this soon because the command asks at once.
        await using var run = ClientRun.Start(simulator.Port, pollSeconds: 60);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0101, Cancel);
        Assert.Equal(0b0101, simulator.Relays);
        await WaitUntilAsync(() => run.Client.State.Relays == 0b0101);

        await run.Client.TurnOffAsync(0b0001, Cancel);
        await WaitUntilAsync(() => run.Client.State.Relays == 0b0100);

        Assert.Contains($"{Vm204Api.RelayOnPath}?key={simulator.Key}&relay=1,3", simulator.ReceivedTargets);
        Assert.Contains($"{Vm204Api.RelayOffPath}?key={simulator.Key}&relay=1", simulator.ReceivedTargets);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(0b1_0000, Cancel));
    }

    [Fact]
    public async Task Should_SeeChangesMadeAtTheBoard_AndReportOnlyThose()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        var reported = run.Changes.Count;

        // Polls that change nothing, not even with the analog value moving, report nothing.
        simulator.SetAnalog(700);
        var requests = simulator.RequestCount;
        await WaitUntilAsync(() => simulator.RequestCount >= requests + 3);
        Assert.Equal(reported, run.Changes.Count);
        await WaitUntilAsync(() => run.Client.State.Status?.Analog == 700);

        await simulator.SwitchRelayAsync(4, true);
        await simulator.SetInputAsync(1, true);
        await WaitUntilAsync(() => run.Client.State.Relays == 0b1000 && run.Client.State.Inputs == 0b0001);

        Assert.Contains(run.Changes, s => s.Relays == 0b1000);
        Assert.Contains(run.Traces, t => t.Direction == BoardTraceDirection.Received && t.Text.StartsWith("Status: relay 4 on", StringComparison.Ordinal) && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Ask for status" && t.IsRoutine);
    }

    [Fact]
    public async Task Should_Reconnect_AfterTheBoardWasUnreachable()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        await WaitUntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal) && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.RejectConnections = false;
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_LogInAgain_WhenTheBoardGetsANewKey()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "VM204");
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        simulator.RegenerateKey();
        var key = simulator.Key;
        await WaitUntilAsync(() => simulator.ReceivedTargets.Contains($"{Vm204Api.StatusPath}?key={key}"));

        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(2, simulator.ReceivedTargets.Count(t => t.StartsWith(Vm204Api.AuthPath, StringComparison.Ordinal)));
        Assert.Contains(run.Traces, t => t.Text == "The board refused the key" && t.IsError);
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = new Vm204Simulator();
        await using var second = new Vm204Simulator();
        first.Start();
        second.Start();
        await second.SwitchRelayAsync(3, true);
        await using var run = ClientRun.Start(first.Port);
        await WaitUntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await WaitUntilAsync(() => run.Client.State.IsOn(3));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
    }

    [Fact]
    public async Task Should_NeverTraceThePasswordOrTheKey()
    {
        const string password = "s3cret.Pw";
        await using var simulator = new Vm204Simulator("admin", password, NullLogger.Instance);
        List<BoardTraceEntry> boardSide = [];
        simulator.Traced += (_, entry) =>
        {
            lock (boardSide)
            {
                boardSide.Add(entry);
            }
        };
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", password, pollSeconds: 60);
        await WaitUntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        await WaitUntilAsync(() => run.Client.State.Relays == 0b0001);

        List<BoardTraceEntry> all;
        lock (boardSide)
        {
            all = [.. run.Traces, .. boardSide];
        }

        Assert.Contains(all, t => t.Bytes == $"GET {Vm204Api.AuthPath}?user=…&password=…");
        Assert.Contains(all, t => t.Bytes == "200 {\"ApiKey\":\"…\"}");
        Assert.All(all, t => Assert.DoesNotContain(password, $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
        Assert.All(all, t => Assert.DoesNotContain(simulator.Key, $"{t.Text} {t.Bytes}", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(password, run.Client.Login.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected()
    {
        using var client = CreateClient(1);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"relays\": [true,false],\"inputs\": [false,false,false,false]}")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]")]
    public async Task Should_NeverConnect_WhenTheStatusIsBroken(string body)
    {
        await using var server = CannedBoard.Start(Encoding.UTF8.GetBytes(body), contentLength: true);
        await using var run = ClientRun.Start(server.Port);

        await WaitUntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The VM204's", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopReading_AHugeAnswer(bool contentLength)
    {
        // A megabyte, with or without saying so up front: the client reads at most 4 KiB of it.
        await using var server = CannedBoard.Start(new byte[1024 * 1024], contentLength);
        await using var run = ClientRun.Start(server.Port);

        await WaitUntilAsync(() => run.Traces.Any(t => t.Text.Contains("longer than 4096 bytes", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public void Should_RefuseSettingsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(80, host: " "));
        Assert.Throws<ArgumentException>(() => CreateClient(80, username: "a&b"));
        Assert.Throws<ArgumentException>(() => CreateClient(80, pollSeconds: 0));
    }

    private static Vm204Client CreateClient(int port, string username = "", string password = "", int pollSeconds = 1, string host = "127.0.0.1")
    {
        var options = new Vm204Options { Host = host, Port = port, Username = username, Password = password, PollSeconds = pollSeconds };
        return new Vm204Client(Options.Create(options), TimeProvider.System, NullLogger<Vm204Client>.Instance);
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

            await Task.Delay(20, Cancel);
        }
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<Vm204State> _changes = [];

        private ClientRun(Vm204Client client, CancellationTokenSource cts)
        {
            Client = client;
            _cts = cts;
            client.Traced += (_, entry) =>
            {
                lock (_traces)
                {
                    _traces.Add(entry);
                }
            };
            client.StateChanged += (_, state) =>
            {
                lock (_changes)
                {
                    _changes.Add(state);
                }
            };
            _task = client.RunAsync(cts.Token);
        }

        public Vm204Client Client { get; }

        public List<BoardTraceEntry> Traces
        {
            get
            {
                lock (_traces)
                {
                    return [.. _traces];
                }
            }
        }

        public List<Vm204State> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public static ClientRun Start(int port, string username = "", string password = "", int pollSeconds = 1)
        {
            return new ClientRun(CreateClient(port, username, password, pollSeconds), new CancellationTokenSource());
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(WaitTimeout);
            _cts.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>A web server that logs anyone in and answers every other request with the same body.</summary>
    private sealed class CannedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private CannedBoard(TcpListener listener, byte[] body, bool contentLength)
        {
            _listener = listener;
            _loop = ServeAsync(body, contentLength);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static CannedBoard Start(byte[] body, bool contentLength)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new CannedBoard(listener, body, contentLength);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync(byte[] body, bool contentLength)
        {
            try
            {
                while (true)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = tcp.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var isLogin = Encoding.ASCII.GetString(buffer, 0, read).StartsWith($"GET {Vm204Api.AuthPath}", StringComparison.Ordinal);
                    var content = isLogin ? Encoding.ASCII.GetBytes(Vm204Json.WriteKey("abc123")) : body;
                    var header = $"HTTP/1.1 200 OK\r\n{(contentLength || isLogin ? $"Content-Length: {content.Length}\r\n" : "")}Connection: close\r\n\r\n";
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                        await stream.WriteAsync(content, _stop.Token);
                    }
                    catch (IOException)
                    {
                        // The client stopped reading a huge answer, as it should.
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        }
    }
}
