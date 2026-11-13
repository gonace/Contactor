using System.Collections.Concurrent;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>The app running Tailscale as the way in, with a fake tailscaled and a clock the test moves.</summary>
public sealed class TailscaleNetworkTests
{
    private const string AuthKey = "tskey-auth-kExample-SECRET1234";
    private const string Address = "relays.tail1234.ts.net";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_JoinWithTheAuthKey_AndServeTheApp()
    {
        var tailscale = new FakeTailscale();
        await using var network = await StartAsync(tailscale, AuthKey, "relays");

        await WaitUntilAsync(() => network.Network.Status.Online);

        Assert.Equal(new TailscaleStatus(true, Address, null), network.Network.Status);
        Assert.Equal([(AuthKey, "relays")], tailscale.LogIns);
        Assert.Contains("serve --bg --https=443 http://127.0.0.1:8080", tailscale.Commands);
        Assert.DoesNotContain(tailscale.Commands, c => c.Contains(AuthKey, StringComparison.Ordinal));
        Assert.DoesNotContain(network.Logger.Lines, l => l.Contains(AuthKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_NotJoinAgain_WhenTheAppIsOnTheNetworkAlready()
    {
        var tailscale = new FakeTailscale { State = "Running" };
        await using var network = await StartAsync(tailscale, AuthKey, "relays");

        await WaitUntilAsync(() => network.Network.Status.Online);

        Assert.Empty(tailscale.LogIns);
        Assert.Contains("set --hostname=relays", tailscale.Commands);
    }

    [Fact]
    public async Task Should_SayHowToTurnOnHttps_AndServeOnceItIsOn()
    {
        var time = new ManualTimeProvider();
        var tailscale = new FakeTailscale
        {
            State = "Running",
            Serve = new TailscaleCommandResult(-1, "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=nExample\n", TimedOut: true),
        };
        await using var network = await StartAsync(tailscale, AuthKey, "relays", time);

        await WaitUntilAsync(() => network.Network.Status.Problem is not null);
        Assert.False(network.Network.Status.Online);
        Assert.Contains("https://login.tailscale.com/f/serve?node=nExample", network.Network.Status.Problem, StringComparison.Ordinal);

        tailscale.Serve = new TailscaleCommandResult(0, "");
        await AdvanceAsync(time, TailscaleNetwork.CheckEvery);
        await WaitUntilAsync(() => network.Network.Status.Online);
        Assert.Null(network.Network.Status.Problem);
    }

    [Fact]
    public async Task Should_SayTheAuthKeyWasRefused_WithoutTheKey_AndNotTryItAgain()
    {
        var time = new ManualTimeProvider();
        var tailscale = new FakeTailscale { LogIn = new TailscaleCommandResult(1, $"backend error: invalid key: {AuthKey} not valid\n") };
        await using var network = await StartAsync(tailscale, AuthKey, "relays", time);

        await WaitUntilAsync(() => network.Network.Status.Problem is not null);
        await AdvanceAsync(time, TailscaleNetwork.CheckEvery);
        await WaitUntilAsync(() => time.PendingTimers == 1);

        Assert.StartsWith("Tailscale didn't take the auth key: backend error: invalid key:", network.Network.Status.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthKey, network.Network.Status.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(network.Logger.Lines, l => l.Contains(AuthKey, StringComparison.Ordinal));
        Assert.Single(tailscale.LogIns);
    }

    [Fact]
    public async Task Should_TryTheAuthKeyAgain_WhenTailscaleDidNotAnswer()
    {
        var time = new ManualTimeProvider();
        var tailscale = new FakeTailscale { LogIn = new TailscaleCommandResult(-1, "", TimedOut: true) };
        await using var network = await StartAsync(tailscale, AuthKey, "relays", time);

        await WaitUntilAsync(() => tailscale.LogIns.Count == 1 && time.PendingTimers == 1);
        tailscale.LogIn = new TailscaleCommandResult(0, "");
        await AdvanceAsync(time, TailscaleNetwork.CheckEvery);

        await WaitUntilAsync(() => network.Network.Status.Online);
        Assert.Equal(2, tailscale.LogIns.Count);
    }

    [Fact]
    public async Task Should_StartAgainWithTheNewSettings_WhenTheyAreSaved()
    {
        var tailscale = new FakeTailscale();
        await using var network = await StartAsync(tailscale, AuthKey, "relays");
        await WaitUntilAsync(() => network.Network.Status.Online);

        tailscale.State = "NeedsLogin";
        Assert.Null(await network.Settings.SaveAsync("tskey-auth-kOther-SECRET5678", "workshop", Cancel));

        await WaitUntilAsync(() => tailscale.DaemonRuns == 2 && tailscale.LogIns.Count == 2);
        Assert.Equal(("tskey-auth-kOther-SECRET5678", "workshop"), tailscale.LogIns[1]);
        Assert.Contains("Tailscale's settings changed: starting tailscaled again with them", network.Logger.Lines);
    }

    [Fact]
    public async Task Should_StartTailscaledAgain_AfterItStops()
    {
        var time = new ManualTimeProvider();
        var tailscale = new FakeTailscale { DaemonExitCode = 1 };
        await using var network = await StartAsync(tailscale, AuthKey, "relays", time);

        // Stopped: offline, and only the wait before the next start is left.
        await WaitUntilAsync(() => network.Logger.Lines.Contains("tailscaled stopped with exit code 1; starting it again in 1 s") && time.PendingTimers == 1);
        Assert.False(network.Network.Status.Online);
        time.Advance(TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => tailscale.DaemonRuns == 2);
    }

    [Fact]
    public async Task Should_GoOffline_WhenItStops()
    {
        var tailscale = new FakeTailscale();
        var network = await StartAsync(tailscale, AuthKey, "relays");
        await WaitUntilAsync(() => network.Network.Status.Online);

        await network.DisposeAsync();

        Assert.Equal(TailscaleStatus.Offline, network.Network.Status);
        Assert.True(tailscale.DaemonCancelled);
    }

    [Theory]
    [InlineData("""{"BackendState":"Running","Self":{"DNSName":"relays.tail1234.ts.net.","Online":true}}""", "Running", Address)]
    [InlineData("""{"BackendState":"NeedsLogin","Self":{"DNSName":""}}""", "NeedsLogin", null)]
    [InlineData("""{"BackendState":"Starting"}""", "Starting", null)]
    [InlineData("Warning: client version \"1.104.7\" != tailscaled server version \"1.104.6\"\n{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"Relays.Tail1234.TS.net.\"}}", "Running", Address)]
    [InlineData("""{"BackendState":"Running","Self":{"DNSName":"evil.example/path?"}}""", "Running", null)]
    [InlineData("""{"BackendState":"Running","Self":{"DNSName":"localhost."}}""", "Running", null)]
    public void Should_ReadTheStateAndAddress_FromStatusJson(string output, string backendState, string? address)
    {
        Assert.Equal(new TailscaleState(backendState, address), TailscaleNetwork.ParseState(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("failed to connect to local tailscaled; it doesn't appear to be running")]
    [InlineData("{not json}")]
    [InlineData("""{"BackendState":5}""")]
    [InlineData("""["Running"]""")]
    public void Should_ReadNoState_FromAnythingElse(string output)
    {
        Assert.Null(TailscaleNetwork.ParseState(output));
    }

    // Moves the clock once only the watch's wait is pending, so the move can't come before the wait starts.
    private static async Task AdvanceAsync(ManualTimeProvider time, TimeSpan by)
    {
        await WaitUntilAsync(() => time.PendingTimers == 1);
        time.Advance(by);
    }

    private static async Task<RunningNetwork> StartAsync(FakeTailscale tailscale, string authKey, string hostName, ManualTimeProvider? time = null)
    {
        var logger = new ListLogger();
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddTailscale(configuration);
            services.AddSingleton<TimeProvider>(time ?? new ManualTimeProvider());
            services.AddSingleton<ITailscaleProcess>(tailscale);
            services.AddSingleton<ILogger<TailscaleNetwork>>(logger);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        var settings = database.Services.GetRequiredService<TailscaleSettings>();
        Assert.Null(await settings.SaveAsync(authKey, hostName, Cancel));
        var network = database.Services.GetRequiredService<TailscaleNetwork>();
        var stop = new CancellationTokenSource();
        var running = network.RunAsync(stop.Token);
        return new RunningNetwork(database, network, settings, logger, stop, running);
    }

    private sealed class RunningNetwork : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        private readonly CancellationTokenSource _stop;
        private readonly Task _running;

        public RunningNetwork(TestDatabase database, TailscaleNetwork network, TailscaleSettings settings, ListLogger logger, CancellationTokenSource stop, Task running)
        {
            _database = database;
            Network = network;
            Settings = settings;
            Logger = logger;
            _stop = stop;
            _running = running;
        }

        public TailscaleNetwork Network { get; }

        public TailscaleSettings Settings { get; }

        public ListLogger Logger { get; }

        // As the app stops: a run that doesn't stop fails the test instead of hanging it.
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            _stop.Dispose();
            await _database.DisposeAsync();
        }
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

            await Task.Delay(10, Cancel);
        }
    }

    // tailscaled and tailscale as the tests need them: "NeedsLogin" until an auth key is taken, then "Running".
    private sealed class FakeTailscale : ITailscaleProcess
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly ConcurrentQueue<(string AuthKey, string HostName)> _logIns = new();
        private int _daemonRuns;

        public volatile string State = "NeedsLogin";

        public TailscaleCommandResult LogIn { get; set; } = new(0, "");

        public TailscaleCommandResult Serve { get; set; } = new(0, "");

        public int? DaemonExitCode { get; init; }

        public int DaemonRuns => Volatile.Read(ref _daemonRuns);

        public bool DaemonCancelled { get; private set; }

        public IReadOnlyList<string> Commands => [.. _commands];

        public IReadOnlyList<(string AuthKey, string HostName)> LogIns => [.. _logIns];

        public async Task<int> RunDaemonAsync(Action<string> output, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _daemonRuns);
            output("Program starting: v1.104.7");
            if (DaemonExitCode is { } exitCode)
            {
                return exitCode;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DaemonCancelled = true;
                throw;
            }

            return 0;
        }

        public Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', arguments);
            _commands.Enqueue(command);
            return Task.FromResult(command switch
            {
                "status --json" => new TailscaleCommandResult(0, StatusJson()),
                _ when command.StartsWith("serve ", StringComparison.Ordinal) => Serve,
                _ => new TailscaleCommandResult(0, ""),
            });
        }

        private string StatusJson()
        {
            var name = State == "Running" ? Address + "." : "";
            return "{\"BackendState\":\"" + State + "\",\"Self\":{\"DNSName\":\"" + name + "\"}}";
        }

        public Task<TailscaleCommandResult> LogInAsync(string authKey, string hostName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _logIns.Enqueue((authKey, hostName));
            if (LogIn.Succeeded)
            {
                State = "Running";
            }

            return Task.FromResult(LogIn);
        }
    }

    private sealed class ListLogger : ILogger<TailscaleNetwork>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _lines.Enqueue(formatter(state, exception));
        }
    }
}
