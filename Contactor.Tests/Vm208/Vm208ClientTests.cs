using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Vm208;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm208;

/// <summary>
/// The client against the simulator, and against fake web servers for answers the simulator never gives. Its clock is
/// a <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class Vm208ClientTests
{
    private const string StatusRequest = "GET /status?interface=-1&socket=0";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheStatus_AskedWithTheParametersInTheFirmwaresOrder()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SetInputAsync(1, true);
        await using var run = ClientRun.Start(simulator.Port);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new Vm208Status(0b0010, true, 0), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(2));
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal(StatusRequest, simulator.ReceivedRequests[0]);
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == 0b0010);
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerRequest_AndTakeTheLastAnswerAsTheState()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0101, Cancel);

        // Confirmed by the command's own answers: no time had to pass for a poll.
        Assert.Equal(0b0101, simulator.Relays);
        Assert.Equal(0b0101, run.Client.State.Relays);
        Assert.Contains(run.Changes, s => s.Relays == 0b0101);

        await run.Client.TurnOffAsync(0b0001, Cancel);
        Assert.Equal(0b0100, run.Client.State.Relays);
        Assert.Equal(
            [
                StatusRequest,
                "POST /relay index=1&state=1&interface=-1&socket=0",
                "POST /relay index=3&state=1&interface=-1&socket=0",
                "POST /relay index=1&state=0&interface=-1&socket=0",
            ],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 3" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Equal(0, simulator.UnansweredCount);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot_AndSendNothingForNone()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(0b1_0000, Cancel));
        await run.Client.TurnOffAsync(0, Cancel);

        Assert.Equal([StatusRequest], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SeeChangesMadeAtTheBoard_OnTheNextPoll_AndReportOnlyThose()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var reported = run.Changes.Count;

        await run.PollAsync(simulator);
        await run.PollAsync(simulator);
        Assert.Equal(reported, run.Changes.Count);
        Assert.Contains(run.Traces, t => t.Text == "Ask for status" && t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Status: all off · input off" && t.IsRoutine);

        await simulator.SwitchRelayAsync(4, true);
        await simulator.SetInputAsync(1, true);
        await run.PollAsync(simulator);

        Assert.Equal((0b1000, true), (run.Client.State.Relays, run.Client.State.Input));
        Assert.Equal(reported + 1, run.Changes.Count);
        Assert.Contains(run.Traces, t => t.Text == "Status: relay 4 on · input on" && !t.IsRoutine);
    }

    [Fact]
    public async Task Should_LoseTheBoard_AfterThreeUnansweredPolls_AndConnectAgain()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        for (var poll = 1; poll <= BoardPoller<Vm208Status>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.PendingTimers == 1);
            run.Time.Advance(TimeSpan.FromSeconds(2));
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal) && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.RejectConnections = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.PendingTimers == 1);
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_CarryOn_WhenTheBoardDropsConnections()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        // Every connection is cut off without an answer (HttpClient tries a request again a few times when that
        // happens), until the poll fails: one failed poll doesn't lose the board.
        board.DropNext = int.MaxValue;
        run.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) && run.Time.PendingTimers == 1);
        board.DropNext = 0;
        Assert.True(run.Client.State.IsConnected);

        var answers = run.Traces.Count(t => t.Text.StartsWith("Status: ", StringComparison.Ordinal));
        run.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("Status: ", StringComparison.Ordinal)) == answers + 1);
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ThrowNotConnected_ForACommandWhoseAnswerIsCutOff()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        board.DropNext = int.MaxValue;
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        Assert.Equal("The VM208 didn't answer; the command may not have been carried out.", error.Message);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected()
    {
        using var client = CreateClient(1, new ManualTimeProvider());

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Channels\":[]}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":false}],\"input\":false}")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]")]
    public async Task Should_NeverConnect_WhenTheStatusIsBroken(string body)
    {
        await using var server = CannedBoard.Start(Encoding.UTF8.GetBytes(body), contentLength: true);
        await using var run = ClientRun.Start(server.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Direction == BoardTraceDirection.Received && t.IsError && t.Text.StartsWith("The VM208", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_NeverConnect_WhenTheBoardAnswersAnError()
    {
        await using var server = CannedBoard.Start([], contentLength: true, statusCode: 500);
        await using var run = ClientRun.Start(server.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "The board answered 500" && t.IsError));

        Assert.False(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopReading_AHugeAnswer(bool contentLength)
    {
        // A megabyte, with or without saying so up front: the client reads at most 8 KiB of it.
        await using var server = CannedBoard.Start(new byte[1024 * 1024], contentLength);
        await using var run = ClientRun.Start(server.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.Contains("longer than 8192 bytes", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_CountARequestTheBoardNeverAnswers_AsAFailedPoll()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        board.HangStatus = true;
        run.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => board.Hanging == 1 && run.Time.PendingTimers == 1);
        run.Time.Advance(Vm208Client.RequestTimeout);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "No answer: The VM208 did not answer within 5 s." && t.IsError));
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SendACommandAtOnce_WhileAPollWaitsForTheBoard()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);
        board.HangStatus = true;
        run.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => board.Hanging == 1);

        await run.Client.TurnOnAsync(0b0010, Cancel);

        Assert.Equal(0b0010, run.Client.State.Relays);
        Assert.Contains(run.Changes, s => s.Relays == 0b0010);
        Assert.Equal(2, board.Requests.Count(r => r.StartsWith(StatusRequest, StringComparison.Ordinal)));
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("No answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LoseABoardThatNeverAnswers_WhileCommandsKeepComing()
    {
        await using var board = ScriptedBoard.Start();
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);
        board.HangAll = true;

        for (var command = 1; command <= 3; command++)
        {
            Assert.True(run.Client.State.IsConnected);
            var sent = run.Client.TurnOnAsync(0b0001, Cancel);
            var expected = command;
            await Wait.UntilAsync(() => board.Hanging == expected && run.Time.PendingTimers == 2);
            run.Time.Advance(Vm208Client.RequestTimeout);
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => sent);
        }

        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The VM208 did not answer within 5 s." && t.IsError);
        Assert.Contains(run.Changes, s => !s.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = new Vm208Simulator();
        await using var second = new Vm208Simulator();
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
    public async Task Should_NeverSendALogin()
    {
        // The firmware asks for basic authentication on its own pages only, never on /status or /relay.
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b1111, Cancel);

        Assert.Equal(0, simulator.AuthorizationCount);
        Assert.Equal(5, simulator.RequestCount);
        Assert.All(run.Traces, t => Assert.DoesNotContain("Authorization", $"{t.Text} {t.Bytes}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Should_RefuseSettingsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(80, TimeProvider.System, host: " "));
        Assert.Throws<ArgumentException>(() => CreateClient(80, TimeProvider.System, pollSeconds: 0));
    }

    private static Vm208Client CreateClient(int port, TimeProvider time, int pollSeconds = 2, string host = "127.0.0.1")
    {
        var options = new Vm208Options { Host = host, Port = port, PollSeconds = pollSeconds };
        return new Vm208Client(Options.Create(options), time, NullLogger<Vm208Client>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<Vm208State> _changes = [];

        private ClientRun(Vm208Client client, ManualTimeProvider time, CancellationTokenSource cts)
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

        public Vm208Client Client { get; }

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

        public List<Vm208State> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public static ClientRun Start(int port, int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, time, pollSeconds), time, new CancellationTokenSource());
        }

        /// <summary>Moves the clock to the next poll, once the client waits for it, and waits until it was answered.</summary>
        public async Task PollAsync(Vm208Simulator simulator)
        {
            await Wait.UntilAsync(() => Time.PendingTimers == 1);
            var answered = simulator.RequestCount;
            Time.Advance(TimeSpan.FromSeconds(2));
            await Wait.UntilAsync(() => simulator.RequestCount == answered + 1 && Time.PendingTimers == 1);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TestContext.Current.CancellationToken);
            _cts.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>A web server that answers every request with the same status code and body.</summary>
    private sealed class CannedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private CannedBoard(TcpListener listener, byte[] body, bool contentLength, int statusCode)
        {
            _listener = listener;
            _loop = ServeAsync(body, contentLength, statusCode);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static CannedBoard Start(byte[] body, bool contentLength, int statusCode = 200)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new CannedBoard(listener, body, contentLength, statusCode);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync(byte[] body, bool contentLength, int statusCode)
        {
            try
            {
                while (true)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = tcp.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    var header = $"HTTP/1.1 {statusCode} X\r\n{(contentLength ? $"Content-Length: {body.Length}\r\n" : "")}Connection: close\r\n\r\n";
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                        await stream.WriteAsync(body, _stop.Token);
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

    /// <summary>
    /// A VM208 the test drives: it switches relays and answers the status, or never answers while
    /// <see cref="HangStatus"/> or <see cref="HangAll"/> is set, or cuts the next request off.
    /// </summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _requests = [];
        private int _relays;
        private int _hanging;
        private int _dropNext;

        private ScriptedBoard(TcpListener listener)
        {
            _listener = listener;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool HangStatus { get; set; }

        /// <summary>Never answers anything: the board is gone.</summary>
        public bool HangAll { get; set; }

        /// <summary>Closes this many of the next connections as soon as their request came, without an answer.</summary>
        public int DropNext
        {
            get => Volatile.Read(ref _dropNext);
            set => Volatile.Write(ref _dropNext, value);
        }

        /// <summary>Requests left without an answer.</summary>
        public int Hanging => Volatile.Read(ref _hanging);

        public List<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
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
                    var request = await ReadRequestAsync(stream);
                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    if (TakeDrop())
                    {
                        return;
                    }

                    var isStatus = request.StartsWith(StatusRequest, StringComparison.Ordinal);
                    if ((isStatus && HangStatus) || HangAll)
                    {
                        Interlocked.Increment(ref _hanging);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }

                    if (!isStatus && Vm208TraceText.ReadRelayForm(request[(request.LastIndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]) is { } command)
                    {
                        var mask = 1 << (command.Relay - 1);
                        if (command.TurnOn)
                        {
                            Interlocked.Or(ref _relays, mask);
                        }
                        else
                        {
                            Interlocked.And(ref _relays, ~mask);
                        }
                    }

                    var content = Encoding.ASCII.GetBytes(Vm208Json.WriteStatus(new Vm208Status((byte)Volatile.Read(ref _relays), false, 0)));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The client gave up, or the test ended.
                }
            }
        }

        private bool TakeDrop()
        {
            while (Volatile.Read(ref _dropNext) is var left and > 0)
            {
                if (Interlocked.CompareExchange(ref _dropNext, left - 1, left) == left)
                {
                    return true;
                }
            }

            return false;
        }

        // The request line, the headers and the form, read until the form is all there.
        private async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return text.ToString();
                }

                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                var all = text.ToString();
                var end = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                var length = all.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } header
                    ? int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                if (all.Length - end - 4 >= length)
                {
                    return all;
                }
            }
        }
    }
}
