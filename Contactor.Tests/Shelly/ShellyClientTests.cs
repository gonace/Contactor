using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Shelly;

/// <summary>
/// The client against the simulator, and against a fake web server for answers the simulator never gives. Each runs on
/// a <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class ShellyClientTests
{
    private const string Password = "s3cret ö-Pw";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ConnectAndReportStatus_WithoutAPassword()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await simulator.SwitchRelayAsync(2, true);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new ShellyStatus(0b0010), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(2));
        Assert.Null(run.Client.State.Problem);
        Assert.Equal([ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, ShellyApi.GetStatus], simulator.Received);
        Assert.Contains(run.Traces, t => t.Text == "Device SIM-4, generation 2, firmware 2.0.0");
        Assert.Contains(run.Traces, t => t.Text == "Status: relay 2 on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerSwitchSet_AndReadAtOnceAfterwards()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101, await run.Client.TurnOnAsync(0b0101, Cancel));
        Assert.Equal(0b0101, simulator.Relays);
        Assert.Equal(0b0101, run.Client.State.Relays);

        Assert.Equal(0b0001, await run.Client.TurnOffAsync(0b0001, Cancel));
        Assert.Equal(0b0100, run.Client.State.Relays);

        // Only Switch.Set, one per relay, each followed by nothing else until the read after the command.
        Assert.Equal(
            [ShellyApi.SwitchSet, ShellyApi.SwitchSet, ShellyApi.GetStatus, ShellyApi.SwitchSet, ShellyApi.GetStatus],
            simulator.Received.Skip(3));
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 3" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 is on (it was off)");
        Assert.Equal(0, await run.Client.TurnOnAsync(0, Cancel));
    }

    [Fact]
    public async Task Should_ReuseTheNonce_OnFirmware2()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        for (var poll = 0; poll < 3; poll++)
        {
            await run.PollAsync();
        }

        await run.Client.TurnOnAsync(0b0011, Cancel);

        // Device info needs no password; the first status gets the one challenge, and every call after it goes signed
        // at once.
        Assert.Equal(1, simulator.ChallengeCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Equal(
            [ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)"],
            simulator.Received.Take(3));
        Assert.All(simulator.Received.Skip(3), r => Assert.EndsWith(" (signed)", r, StringComparison.Ordinal));
        Assert.Equal(0b0011, simulator.Relays);
    }

    [Fact]
    public async Task Should_AnswerAChallengeForEveryRequest_OnFirmware17()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password, firmware: "1.7.1");
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.PollAsync();

        await run.Client.TurnOnAsync(0b0010, Cancel);

        // Status at connect, the first poll, a poll, the Switch.Set and the read after it: five challenges, each answered.
        Assert.Equal(5, simulator.ChallengeCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Equal(
            [ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)", ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)"],
            simulator.Received.Take(5));
        Assert.Equal(0b0010, simulator.Relays);
        Assert.True(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_TakeANewNonceOnce_WhenTheDeviceRefusesTheKeptOne(bool stale)
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var challenges = simulator.ChallengeCount;

        if (stale)
        {
            simulator.ExpireNonces();
        }
        else
        {
            simulator.ForgetNonces();
        }

        await run.PollAsync();
        await run.PollAsync();

        // One refusal, its new challenge answered at once, and the new nonce kept for the poll after.
        Assert.Equal(challenges + 1, simulator.ChallengeCount);
        Assert.Equal(stale ? 1 : 0, simulator.StaleCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly wants a new nonce");
        Assert.DoesNotContain(run.Traces, t => t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_StayDisconnectedAndRetry_WhenThePasswordIsWrong()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, "wrong");

        // Refused, then tried again after the wait: only when the clock gets there.
        await Wait.UntilAsync(() => simulator.DeniedCount == 1 && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => simulator.DeniedCount == 2 && run.Time.WaitsOnly(TimeSpan.FromSeconds(2)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, ShellyModels.OneRelay), run.Client.State.Problem);
        Assert.Single(run.Changes, c => c.Problem is not null);
        Assert.Contains(run.Traces, t => t.Text == "Wrong password" && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        // A new password connects at once, and clears the problem.
        run.Client.SetPassword(Password);
        Assert.Null(run.Client.State.Problem);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SayAPasswordIsNeeded_WhenTheAppHasNone()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginNeeded, ShellyModels.OneRelay), run.Client.State.Problem);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly asks for a password. Enter it on the Settings screen." && t.IsError);
    }

    [Fact]
    public async Task Should_SayToWait_AndNeverRetryWithinARequest_OnA429()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        simulator.Throttle = true;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));

        Assert.Equal(1, simulator.ThrottledCount);
        Assert.Equal([ShellyApi.GetDeviceInfo, ShellyApi.GetStatus], simulator.Received);
        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("The Shelly asks to wait", StringComparison.Ordinal) && t.IsError);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: The Shelly asks to wait", StringComparison.Ordinal));

        simulator.Throttle = false;
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(null, "2.0.0", null)]
    [InlineData(1, "2.0.0", null)]
    [InlineData(2, "2.0.0", "cover")]
    public async Task Should_RefuseADeviceThatIsntARelayShelly(int? generation, string firmware, string? profile)
    {
        await using var simulator = Start(ShellyModels.TwoRelays, firmware: firmware);
        simulator.Generation = generation;
        simulator.Profile = profile;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays), run.Client.State.Problem);
        Assert.False(run.Client.State.IsConnected);
        var reason = profile is null ? "isn't a Shelly of the second generation or later" : "set up to drive a cover";
        Assert.Contains(run.Traces, t => t.Text.Contains(reason, StringComparison.Ordinal) && t.IsError);
        Assert.DoesNotContain(ShellyApi.SwitchSet, simulator.Received);
    }

    [Theory]
    [InlineData("1.6.2")]
    [InlineData("0.6.7")]
    [InlineData("beta")]
    public async Task Should_RefuseFirmwareBefore170(string firmware)
    {
        await using var simulator = Start(ShellyModels.OneRelay, firmware: firmware);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The Shelly's firmware is too old. Update it to 1.7.0", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Problem);
        Assert.Equal([ShellyApi.GetDeviceInfo], simulator.Received);
    }

    [Fact]
    public async Task Should_TakeFirmware170()
    {
        await using var simulator = Start(ShellyModels.OneRelay, firmware: "1.7.0");
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseADeviceWithAnotherNumberOfRelays()
    {
        await using var simulator = Start(ShellyModels.FourRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays), run.Client.State.Problem);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly at this address has 4 relays, not 2." && t.IsError);
        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LoseADevice_WhoseSwitchesChangeWhileItRuns()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        // Set to the cover profile at its own web page: three polls in a row fail, and the connect after says why.
        simulator.Profile = "cover";
        for (var poll = 0; poll < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost; poll++)
        {
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
            run.Time.Advance(run.PollInterval);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        await Wait.UntilAsync(() => run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.Problem is not null);
        Assert.Contains(run.Traces, t => t.Text.Contains("drive a cover", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ShowTheDevicesOwnMessage_WhenItRefusesASwitchSet()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0010, Cancel));
        Assert.Equal("The Shelly didn't take the command: relay 2: Overtemp condition present (error -109)", error.Message);
        Assert.Contains(run.Traces, t => t.Text == "Refused: Overtemp condition present (error -109)" && t.IsError);

        // The other relay still goes, and only it counts as taken.
        Assert.Equal(0b0001, await run.Client.TurnOnAsync(0b0011, Cancel));
        Assert.Equal(0b0001, simulator.Relays);

        // A refusal is an answer: never a lost device, however many come.
        for (var i = 0; i < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost + 1; i++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffAsync(0b0010, Cancel));
        }

        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_Reconnect_AfterTheDeviceWasUnreachable()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        for (var poll = 1; poll <= BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
            run.Time.Advance(run.PollInterval);
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
    public async Task Should_NeverTraceThePasswordOrTheDigest()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        List<BoardTraceEntry> deviceSide = [];
        simulator.Traced += (_, entry) =>
        {
            lock (deviceSide)
            {
                deviceSide.Add(entry);
            }
        };
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);

        List<BoardTraceEntry> all;
        lock (deviceSide)
        {
            all = [.. run.Traces, .. deviceSide];
        }

        Assert.Contains(all, t => t.Bytes?.EndsWith(" · with the password's digest", StringComparison.Ordinal) == true);
        Assert.All(all, t => Assert.DoesNotContain(Password, $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
        Assert.All(all, t => Assert.DoesNotContain("response=", $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
        Assert.DoesNotContain(Password, run.Client.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_MoveToAnotherDevice_WhenTheAddressChanges()
    {
        await using var first = Start(ShellyModels.OneRelay);
        await using var second = Start(ShellyModels.OneRelay);
        await second.SwitchRelayAsync(1, true);
        await using var run = ClientRun.Start(first, ShellyModels.OneRelay);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(1));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected_AndRelaysTheEntryHasNot()
    {
        using var client = CreateClient(1, ShellyModels.TwoRelays);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TurnOnAsync(0b0100, Cancel));
    }

    [Fact]
    public void Should_RefuseSettingsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(80, ShellyModels.OneRelay, password: new string('p', ShellyApi.PasswordMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreateClient(80, ShellyModels.OneRelay, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => CreateClient(80, Contactor.Boards.Devantech.DevantechModels.Eth002));
        using var client = CreateClient(80, ShellyModels.OneRelay, host: "");
        Assert.False(client.Endpoint.HasHost);
        Assert.Throws<ArgumentException>(() => client.SetPassword(new string('p', ShellyApi.PasswordMaxLength + 1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopReading_AHugeAnswer(bool contentLength)
    {
        // A megabyte, with or without saying so up front: the client reads at most 64 kB of it.
        await using var server = CannedDevice.Start(new byte[1024 * 1024], contentLength);
        await using var run = ClientRun.Start(server.Port, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.Contains("longer than 65536 bytes", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"id\":999,\"result\":{\"id\":\"shelly-x\",\"gen\":2,\"ver\":\"2.0.0\"}}")]
    public async Task Should_NeverConnect_WhenTheAnswerIsBroken(string body)
    {
        await using var server = CannedDevice.Start(Encoding.UTF8.GetBytes(body), contentLength: true);
        await using var run = ClientRun.Start(server.Port, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The Shelly's answer", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_GiveUpOnARequest_TheDeviceNeverAnswers()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);
        var time = run.Time;
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.WaitsOnly(run.PollInterval));

        // A device that takes the connection and says nothing: its own wait for the request is longer than ours.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port));
        await Wait.UntilAsync(() => time.WaitsOnly(ShellyClient.RequestTimeout));
        time.Advance(ShellyClient.RequestTimeout);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "No answer: The Shelly did not answer within 5 s." && t.IsError));
    }

    private static ShellySimulator Start(BoardModel model, string? password = null, string firmware = "2.0.0")
    {
        var simulator = new ShellySimulator(model, password, NullLogger.Instance) { Firmware = firmware };
        simulator.Start();
        return simulator;
    }

    private static ShellyClient CreateClient(int port, BoardModel model, string password = "", int pollSeconds = 2, string host = "127.0.0.1", TimeProvider? time = null)
    {
        var options = new ShellyOptions { Host = host, Port = port, Password = password, PollSeconds = pollSeconds };
        return new ShellyClient(Options.Create(options), model, time ?? new ManualTimeProvider(), NullLogger<ShellyClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<ShellyState> _changes = [];

        private ClientRun(ShellyClient client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
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

        public ShellyClient Client { get; }

        public ManualTimeProvider Time { get; }

        public TimeSpan PollInterval { get; }

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

        public List<ShellyState> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public static ClientRun Start(ShellySimulator simulator, BoardModel model, string password = "", int pollSeconds = 2)
        {
            return Start(simulator.Port, model, password, pollSeconds);
        }

        public static ClientRun Start(int port, BoardModel model, string password = "", int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, model, password, pollSeconds, time: time), time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
        }

        /// <summary>
        /// Moves the clock to the next poll, once the client waits for it, and waits until the device answered it and the
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

    /// <summary>A web server that answers every request with the same body and 200.</summary>
    private sealed class CannedDevice : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private CannedDevice(TcpListener listener, byte[] body, bool contentLength)
        {
            _listener = listener;
            _loop = ServeAsync(body, contentLength);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static CannedDevice Start(byte[] body, bool contentLength)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new CannedDevice(listener, body, contentLength);
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
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    var header = $"HTTP/1.1 200 OK\r\n{(contentLength ? $"Content-Length: {body.Length}\r\n" : "")}Connection: close\r\n\r\n";
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped.
            }
        }
    }
}
