using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Vm204;
using Contactor.Tests.Vm208;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm204;

/// <summary>
/// The client against the simulator, and against a fake web server for answers the simulator never gives. Each runs on
/// a <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class Vm204ClientTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

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

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0, simulator.DeniedLoginCount);
        Assert.Contains($"{Vm204Api.AuthPath}?user={username}&password={password}", simulator.ReceivedTargets);
    }

    [Fact]
    public async Task Should_StayDisconnectedAndRetry_WhenThePasswordIsWrong()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "vm204");

        // Refused, then tried again after the wait: only when the clock gets there.
        await Wait.UntilAsync(() => simulator.DeniedLoginCount == 1 && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => simulator.DeniedLoginCount == 2 && run.Time.WaitsOnly(TimeSpan.FromSeconds(2)));

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
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0101, Cancel);
        Assert.Equal(0b0101, simulator.Relays);
        await Wait.UntilAsync(() => run.Client.State.Relays == 0b0101);

        await run.Client.TurnOffAsync(0b0001, Cancel);
        await Wait.UntilAsync(() => run.Client.State.Relays == 0b0100);

        Assert.Contains($"{Vm204Api.RelayOnPath}?key={simulator.Key}&relay=1,3", simulator.ReceivedTargets);
        Assert.Contains($"{Vm204Api.RelayOffPath}?key={simulator.Key}&relay=1", simulator.ReceivedTargets);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(0b1_0000, Cancel));
    }

    [Fact]
    public async Task Should_SeeChangesMadeAtTheBoard_AndReportOnlyThose()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var reported = run.Changes.Count;

        // Polls that change nothing, not even with the analog value moving, report nothing.
        simulator.SetAnalog(700);
        for (var poll = 0; poll < 3; poll++)
        {
            await run.PollAsync();
        }

        Assert.Equal(reported, run.Changes.Count);
        Assert.Equal(700, run.Client.State.Status?.Analog);

        await simulator.SwitchRelayAsync(4, true);
        await simulator.SetInputAsync(1, true);
        await run.PollAsync();
        Assert.Equal((0b1000, 0b0001), (run.Client.State.Relays, run.Client.State.Inputs));

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
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        for (var poll = 1; poll <= BoardPoller<Vm204Status>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(PollInterval));
            run.Time.Advance(PollInterval);
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal) && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.RejectConnections = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_LogInAgain_WhenTheBoardGetsANewKey()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "VM204");
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(PollInterval));

        // The next poll is refused and drops the key; the one after it logs in again and asks with the new key.
        simulator.RegenerateKey();
        var key = simulator.Key;
        run.Time.Advance(PollInterval);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "The board refused the key") && run.Time.WaitsOnly(PollInterval));
        await run.PollAsync();

        Assert.Contains($"{Vm204Api.StatusPath}?key={key}", simulator.ReceivedTargets);
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
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(3));
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
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        await Wait.UntilAsync(() => run.Client.State.Relays == 0b0001);

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

        // The first poll fails, and so does each one after it, until the third in a row loses the connection.
        for (var poll = 1; poll < BoardPoller<Vm204Status>.DefaultFailuresBeforeLost; poll++)
        {
            await Wait.UntilAsync(() => run.Time.WaitsOnly(PollInterval));
            run.Time.Advance(PollInterval);
        }

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The VM204's", StringComparison.Ordinal)));

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

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.Contains("longer than 4096 bytes", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_CountARequestTheBoardNeverAnswers_AsAFailedPoll()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        var time = run.Time;
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.PendingTimers == 1);

        board.HangStatus = true;
        time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => board.Hanging == 1 && time.PendingTimers == 1);
        time.Advance(Vm204Client.RequestTimeout);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "No answer: The VM204 did not answer within 5 s." && t.IsError));
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SendACommandAtOnce_WhileAPollWaitsForTheBoard()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        var time = run.Time;
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.PendingTimers == 1);
        board.HangStatus = true;
        time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => board.Hanging == 1);
        var statusRequests = board.Targets.Count(t => t.StartsWith(Vm204Api.StatusPath, StringComparison.Ordinal));

        await run.Client.TurnOnAsync(0b0010, Cancel);

        // Confirmed by the command's own answer: nothing else was asked, and no time had to pass.
        Assert.Equal(0b0010, run.Client.State.Relays);
        Assert.Contains(run.Changes, s => s.Relays == 0b0010);
        Assert.Equal(statusRequests, board.Targets.Count(t => t.StartsWith(Vm204Api.StatusPath, StringComparison.Ordinal)));
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("No answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LoseABoardThatNeverAnswers_AfterThreeRequestsInARow()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        var time = run.Time;
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.PendingTimers == 1);
        board.HangAll = true;

        // A command without an answer (1), and the status read it brings at once, without an answer too (2).
        var first = run.Client.TurnOnAsync(0b0001, Cancel);
        await Wait.UntilAsync(() => board.Hanging == 1 && time.PendingTimers == 2);
        time.Advance(Vm204Client.RequestTimeout);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => first);
        await Wait.UntilAsync(() => board.Hanging == 2 && time.PendingTimers == 1);
        Assert.StartsWith(Vm204Api.StatusPath, board.Targets[^1], StringComparison.Ordinal);
        time.Advance(Vm204Client.RequestTimeout);
        await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) == 2 && time.PendingTimers == 1);
        Assert.True(run.Client.State.IsConnected);

        // The next command is the third in a row.
        var third = run.Client.TurnOnAsync(0b0001, Cancel);
        await Wait.UntilAsync(() => board.Hanging == 3 && time.PendingTimers == 2);
        time.Advance(Vm204Client.RequestTimeout);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => third);

        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The VM204 did not answer within 5 s." && t.IsError);
        Assert.Contains(run.Changes, s => !s.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
    }

    [Fact]
    public async Task Should_LoseABoardThatAnswersBrokenJson_CountingCommandsAsPolls()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(TimeSpan.FromSeconds(60)));
        board.BrokenAnswers = true;

        // A command whose answer can't be read (1), and the status read it brings at once, as broken (2).
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
        Assert.StartsWith("The VM204 didn't take the command: ", error.Message, StringComparison.Ordinal);
        await Wait.UntilAsync(() => board.Targets.Count(t => t.StartsWith(Vm204Api.StatusPath, StringComparison.Ordinal)) == 2
            && run.Time.WaitsOnly(TimeSpan.FromSeconds(60)));
        Assert.True(run.Client.State.IsConnected);

        // The next command is the third failure in a row: the board is lost without waiting for a poll.
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: The VM204's", StringComparison.Ordinal) && t.IsError);
    }

    [Fact]
    public async Task Should_LogInAtOnce_WhenACommandFindsNoKey()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "VM204", pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var oldKey = simulator.Key;

        // A poll the board refuses drops the key; one refused poll doesn't lose the board.
        simulator.RegenerateKey();
        run.Client.Refresh();
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "The board refused the key"));
        Assert.True(run.Client.State.IsConnected);
        Assert.Contains($"{Vm204Api.StatusPath}?key={oldKey}", simulator.ReceivedTargets);

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
        Assert.Equal("The VM204 is logging in again; the command was not sent.", error.Message);

        // No wait for the poll a minute away: the login comes straight after.
        await Wait.UntilAsync(() => simulator.ReceivedTargets.Count(t => t.StartsWith(Vm204Api.AuthPath, StringComparison.Ordinal)) == 2);
        await Wait.UntilAsync(() => simulator.ReceivedTargets.Contains($"{Vm204Api.StatusPath}?key={simulator.Key}"));
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_DropTheKey_WhenACommandIsRefused_AndLogInAgain()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "VM204", pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.RegenerateKey();
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        // The refusal asks for a poll at once, which logs in again: no wait for the next poll a minute away.
        var key = simulator.Key;
        await Wait.UntilAsync(() => simulator.ReceivedTargets.Contains($"{Vm204Api.StatusPath}?key={key}"));
        Assert.Equal(0, simulator.Relays);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_LogInAgainAtOnce_WithANewLogin()
    {
        await using var simulator = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, "admin", "wrong", pollSeconds: 60);
        await Wait.UntilAsync(() => simulator.DeniedLoginCount == 1);

        run.Client.SetLogin(Vm204Login.TryCreate("admin", "VM204", out _)!);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal("admin", run.Client.Login.Username);
        Assert.Contains($"{Vm204Api.AuthPath}?user=admin&password=VM204", simulator.ReceivedTargets);
    }

    // A new installation gets the board's address in the setup guide: the app starts without one.
    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        using var client = CreateClient(80, host: "");

        Assert.False(client.Endpoint.HasHost);
    }

    [Fact]
    public void Should_RefuseSettingsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(80, username: "a&b"));
        Assert.Throws<ArgumentException>(() => CreateClient(80, pollSeconds: 0));
    }

    private static Vm204Client CreateClient(int port, string username = "", string password = "", int pollSeconds = 2, string host = "127.0.0.1", TimeProvider? time = null)
    {
        var options = new Vm204Options { Host = host, Port = port, Username = username, Password = password, PollSeconds = pollSeconds };
        return new Vm204Client(Options.Create(options), time ?? new ManualTimeProvider(), NullLogger<Vm204Client>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<Vm204State> _changes = [];

        private ClientRun(Vm204Client client, ManualTimeProvider time, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
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

        public ManualTimeProvider Time { get; }

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

        public static ClientRun Start(int port, string username = "", string password = "", int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, username, password, pollSeconds, time: time), time, new CancellationTokenSource());
        }

        /// <summary>
        /// Moves the clock to the next poll, once the client waits for it, and waits until the board answered it and the
        /// client waits for the one after.
        /// </summary>
        public async Task PollAsync()
        {
            await Wait.UntilAsync(() => Time.WaitsOnly(PollInterval));
            Time.Advance(PollInterval);
            await Wait.UntilAsync(() => Client.State.LastContactUtc == Time.GetUtcNow() && Time.WaitsOnly(PollInterval));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TestContext.Current.CancellationToken);
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped. A loop that was still answering when the listener stopped finds it "not listening".
            }
        }
    }

    /// <summary>
    /// A VM204 the test drives: it logs anyone in, switches relays, and answers the status, or never answers it while
    /// <see cref="HangStatus"/> is set.
    /// </summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _targets = [];
        private int _relays;
        private int _hanging;

        private ScriptedBoard(TcpListener listener)
        {
            _listener = listener;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool HangStatus { get; set; }

        /// <summary>Never answers anything but a login: the board is gone.</summary>
        public bool HangAll { get; set; }

        /// <summary>Answers everything but a login with a body that isn't JSON, after switching what was asked.</summary>
        public bool BrokenAnswers { get; set; }

        /// <summary>Status requests left without an answer.</summary>
        public int Hanging => Volatile.Read(ref _hanging);

        public List<string> Targets
        {
            get
            {
                lock (_targets)
                {
                    return [.. _targets];
                }
            }
        }

        public static ScriptedBoard Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ScriptedBoard(listener);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            List<Task> requests = [];
            try
            {
                while (true)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    requests.Add(AnswerAsync(tcp));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped. A loop that was still answering when the listener stopped finds it "not listening".
            }

            await Task.WhenAll(requests);
        }

        private async Task AnswerAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    var stream = tcp.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var target = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                    lock (_targets)
                    {
                        _targets.Add(target);
                    }

                    string body;
                    if (target.StartsWith(Vm204Api.AuthPath, StringComparison.Ordinal))
                    {
                        body = Vm204Json.WriteKey("abc123");
                    }
                    else if ((target.StartsWith(Vm204Api.StatusPath, StringComparison.Ordinal) && HangStatus) || HangAll)
                    {
                        Interlocked.Increment(ref _hanging);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }
                    else
                    {
                        if (target.StartsWith(Vm204Api.RelayOnPath, StringComparison.Ordinal))
                        {
                            Interlocked.Or(ref _relays, Vm204TraceText.RelayMask(target.Split('?')[1]));
                        }

                        body = BrokenAnswers ? "not json" : Vm204Json.WriteStatus(new Vm204Status((byte)Volatile.Read(ref _relays), 0, 0));
                    }

                    var content = Encoding.ASCII.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The client gave up, or the test ended.
                }
            }
        }
    }
}
