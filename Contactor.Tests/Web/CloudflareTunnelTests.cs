using System.Collections.Concurrent;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>The app keeping cloudflared running, with a fake process and a clock the test moves.</summary>
public sealed class CloudflareTunnelTests
{
    private const string Token = "eyJhIjoiZXhhbXBsZSJ9";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_StartNothing_WithoutAToken()
    {
        var process = new FakeCloudflared();
        var logger = new ListLogger();
        await using var service = await CreateAsync("  ", process, new ManualTimeProvider(), logger);

        await service.StartAsync(Cancel);

        // It waits for a token from the setup guide, and stops with the app.
        await WaitUntilAsync(() => logger.Lines.Contains("No tunnel token yet: cloudflared waits for one"));
        await service.StopAsync(Cancel);
        Assert.Equal(0, process.Runs);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Should_RunCloudflared_WithTheTokenAndPath_AndLogItsLines()
    {
        var process = new FakeCloudflared { Output = ["Registered tunnel connection", "2026-11-12T10:00:00Z ERR Failed to dial a quic connection"] };
        var logger = new ListLogger();
        await using var service = await CreateAsync($" {Token} ", process, new ManualTimeProvider(), logger);

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        await service.StopAsync(Cancel);

        Assert.Equal(("/opt/cloudflared", Token), (process.LastPath, process.LastToken));
        Assert.Contains("cloudflared: Registered tunnel connection", logger.Lines);
        Assert.Contains((LogLevel.Warning, "cloudflared: 2026-11-12T10:00:00Z ERR Failed to dial a quic connection"), logger.Entries);
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_StartItAgain_AfterAGrowingWait()
    {
        var time = new ManualTimeProvider();
        var process = new FakeCloudflared { ExitCode = 1 };
        var logger = new ListLogger();
        await using var service = await CreateAsync(Token, process, time, logger);

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        foreach (var (seconds, runs) in new[] { (1, 2), (2, 3), (4, 4), (8, 5), (16, 6), (32, 7), (60, 8), (60, 9) })
        {
            await AdvanceAsync(time, TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(20, Cancel);
            Assert.Equal(runs - 1, process.Runs);
            await AdvanceAsync(time, TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(() => process.Runs == runs);
        }

        await service.StopAsync(Cancel);
        Assert.Contains("cloudflared stopped with exit code 1; starting it again in 1 s", logger.Lines);
        Assert.Contains("cloudflared stopped with exit code 1; starting it again in 60 s", logger.Lines);
    }

    [Fact]
    public async Task Should_StartAgainSoon_AfterARunThatWorkedForAWhile()
    {
        var time = new ManualTimeProvider();
        var process = new FakeCloudflared { ExitCode = 1 };
        var logger = new ListLogger();
        await using var service = await CreateAsync(Token, process, time, logger);
        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => process.Runs == 2);
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => process.Runs == 3);

        // The next run lasts 10 minutes before it stops: the wait starts from 1 s again, not 8 s.
        process.Duration = (TimeSpan.FromMinutes(10), time);
        await AdvanceAsync(time, TimeSpan.FromSeconds(4));
        await WaitUntilAsync(() => process.Runs == 4);
        process.Duration = null;
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => process.Runs == 5);
        await service.StopAsync(Cancel);
        Assert.Equal(
            ["1", "2", "4", "1"],
            logger.Lines.Where(l => l.StartsWith("cloudflared stopped", StringComparison.Ordinal)).Take(4).Select(l => l.Split(' ')[^2]));
    }

    [Fact]
    public async Task Should_KeepTrying_WhenCloudflaredCannotStart()
    {
        var time = new ManualTimeProvider();
        var process = new FakeCloudflared { Failure = new System.ComponentModel.Win32Exception("No such file or directory") };
        var logger = new ListLogger();
        await using var service = await CreateAsync(Token, process, time, logger);

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => process.Runs == 2);
        await service.StopAsync(Cancel);

        Assert.Contains("Could not run cloudflared; trying again in 1 s", logger.Lines);
    }

    [Fact]
    public async Task Should_StopCloudflared_WhenTheAppStops()
    {
        var process = new FakeCloudflared { RunUntilCancelled = true };
        await using var service = await CreateAsync(Token, process, new ManualTimeProvider(), new ListLogger());

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        await service.StopAsync(Cancel);

        Assert.True(process.WasCancelled);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // The setup guide shows "Tunnel connected" once cloudflared says so, and not after it stops.
    [Fact]
    public async Task Should_SayConnected_WhileCloudflaredIsConnected()
    {
        var process = new FakeCloudflared { Output = ["INF Registered tunnel connection connIndex=0"], RunUntilCancelled = true };
        await using var service = await CreateAsync(Token, process, new ManualTimeProvider(), new ListLogger());
        var tunnel = service.Services.GetRequiredService<CloudflareTunnel>();
        var changes = 0;
        tunnel.ConnectedChanged += (_, _) => Interlocked.Increment(ref changes);
        Assert.False(tunnel.Connected);

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => tunnel.Connected);

        // The event comes just after the state, on cloudflared's own thread.
        await WaitUntilAsync(() => Volatile.Read(ref changes) == 1);

        await service.StopAsync(Cancel);
        Assert.False(tunnel.Connected);
        Assert.Equal(2, changes);
    }

    // The setup guide saves a new token: cloudflared starts again with it at once, without the wait after a failure.
    [Fact]
    public async Task Should_StartAgainAtOnce_WithANewToken()
    {
        var process = new FakeCloudflared { RunUntilCancelled = true };
        var logger = new ListLogger();
        await using var service = await CreateAsync(Token, process, new ManualTimeProvider(), logger);
        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);

        var problem = await service.Services.GetRequiredService<CloudflareSettings>().SaveTunnelAsync(" new-token ", "https://Relays.Example.com/", Cancel);

        Assert.Null(problem);
        await WaitUntilAsync(() => process.Runs == 2);
        Assert.Equal("new-token", process.LastToken);
        Assert.True(process.WasCancelled);
        Assert.Equal("relays.example.com", service.Services.GetRequiredService<CloudflareSettings>().Tunnel.Address);
        Assert.Contains("The tunnel's token changed: starting cloudflared again with it", logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("new-token", StringComparison.Ordinal));
    }

    // Saving the same token again (e.g. only the address changed) doesn't drop the connection through the tunnel.
    [Fact]
    public async Task Should_KeepTheTunnel_WhenTheSameTokenIsSaved()
    {
        var process = new FakeCloudflared { RunUntilCancelled = true };
        var logger = new ListLogger();
        await using var service = await CreateAsync(Token, process, new ManualTimeProvider(), logger);
        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);

        var settings = service.Services.GetRequiredService<CloudflareSettings>();
        var changes = 0;
        settings.TunnelChanged += (_, _) => Interlocked.Increment(ref changes);

        await settings.SaveTunnelAsync(Token, "other.example.com", Cancel);
        Assert.Equal(0, changes);

        // A new token does start it again, once.
        await settings.SaveTunnelAsync("new-token", "other.example.com", Cancel);
        await WaitUntilAsync(() => process.Runs == 2);
        Assert.Equal(1, changes);
        Assert.Single(logger.Lines, line => line == "The tunnel's token changed: starting cloudflared again with it");
    }

    [Fact]
    public async Task Should_StartTheTunnel_WhenATokenIsSaved()
    {
        var process = new FakeCloudflared { RunUntilCancelled = true };
        var logger = new ListLogger();
        await using var service = await CreateAsync("", process, new ManualTimeProvider(), logger);
        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => logger.Lines.Contains("No tunnel token yet: cloudflared waits for one"));

        await service.Services.GetRequiredService<CloudflareSettings>().SaveTunnelAsync(Token, "relays.example.com", Cancel);

        await WaitUntilAsync(() => process.Runs == 1);
        Assert.Equal(Token, process.LastToken);
    }

    // A new installation has no way in until the setup guide chooses one: then it starts at once.
    [Fact]
    public async Task Should_StartTheWayIn_WhenOneIsChosen()
    {
        var process = new FakeCloudflared { RunUntilCancelled = true };
        await using var service = await CreateAsync(Token, process, new ManualTimeProvider(), new ListLogger(), chosen: false);
        await service.StartAsync(Cancel);
        Assert.Equal(0, process.Runs);

        await service.Services.GetRequiredService<AccessChoice>().ChooseAsync(AccessCatalogue.Find(CloudflareMethods.TunnelId)!, Cancel);

        await WaitUntilAsync(() => process.Runs == 1);
        await using var db = await service.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        Assert.Equal(CloudflareMethods.TunnelId, (await db.Installations.SingleAsync(Cancel)).AccessType);
    }

    // Moves the clock once the service is waiting, so the move can't come before its wait starts.
    private static async Task AdvanceAsync(ManualTimeProvider time, TimeSpan by)
    {
        await WaitUntilAsync(() => time.PendingTimers == 1);
        time.Advance(by);
    }

    // The tunnel as the app runs it: Cloudflare saved as the way in, kept running by AccessHostedService for the
    // app's lifetime.
    private static async Task<RunningTunnel> CreateAsync(string token, FakeCloudflared process, TimeProvider time, ListLogger logger, bool chosen = true)
    {
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
            services.AddCloudflare(configuration);
            services.AddAccess(configuration);
            services.Configure<TunnelOptions>(o =>
            {
                o.Token = token;
                o.Path = "/opt/cloudflared";
            });
            services.AddSingleton(time);
            services.AddSingleton<ICloudflaredProcess>(process);
            services.AddSingleton<ILogger<CloudflareTunnel>>(logger);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Installations.Add(new Installation { AccessType = chosen ? CloudflareMethods.TunnelId : null });
            await db.SaveChangesAsync(Cancel);
        }

        await database.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(Cancel);
        return new RunningTunnel(database, database.Services.GetServices<IHostedService>().OfType<AccessHostedService>().Single());
    }

    private sealed class RunningTunnel : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        private readonly AccessHostedService _service;

        public RunningTunnel(TestDatabase database, AccessHostedService service)
        {
            _database = database;
            _service = service;
        }

        public Task? ExecuteTask => _service.ExecuteTask;

        public IServiceProvider Services => _database.Services;

        public Task StartAsync(CancellationToken cancellationToken) => _service.StartAsync(cancellationToken);

        // As the app stops: it waits only so long, so a service that doesn't stop cloudflared fails a test instead
        // of hanging it.
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(10));
            await _service.StopAsync(stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            _service.Dispose();
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

    private sealed class FakeCloudflared : ICloudflaredProcess
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public int ExitCode { get; init; }

        public IReadOnlyList<string> Output { get; init; } = [];

        public Exception? Failure { get; init; }

        public bool RunUntilCancelled { get; init; }

        // When set, the next run moves the clock this far before it stops, as if it had run that long.
        public (TimeSpan Length, ManualTimeProvider Time)? Duration { get; set; }

        public bool WasCancelled { get; private set; }

        public string? LastPath { get; private set; }

        public string? LastToken { get; private set; }

        public async Task<int> RunAsync(string path, string token, Action<string> output, CancellationToken cancellationToken)
        {
            (LastPath, LastToken) = (path, token);
            foreach (var line in Output)
            {
                output(line);
            }

            if (Duration is { } duration)
            {
                duration.Time.Advance(duration.Length);
            }

            Interlocked.Increment(ref _runs);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (RunUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    throw;
                }
            }

            return ExitCode;
        }
    }

    private sealed class ListLogger : ILogger<CloudflareTunnel>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Line)> Entries => [.. _entries];

        public IReadOnlyList<string> Lines => [.. _entries.Select(e => e.Line)];

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
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>A clock that only moves when the test moves it, with timers that fire when it passes their time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>How many timers wait to fire.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Whether a timer waits that is due this long from now, among any others.</summary>
    public bool Waits(TimeSpan dueIn)
    {
        lock (_lock)
        {
            return _timers.Any(t => t.DueAt == _now + dueIn);
        }
    }

    /// <summary>
    /// Whether exactly one timer waits, due this long from now: e.g. a poller's wait for its next poll, and not a
    /// request's timeout that is still running.
    /// </summary>
    public bool WaitsOnly(TimeSpan dueIn)
    {
        lock (_lock)
        {
            return _timers.Count == 1 && _timers[0].DueAt == _now + dueIn;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt <= _now)];
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
            }
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _owner.Schedule(this, dueTime);
            return true;
        }

        public void Fire()
        {
            _callback(_state);
        }

        public void Dispose()
        {
            _owner.Schedule(this, Timeout.InfiniteTimeSpan);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The real process wrapper, with the dotnet command standing in for cloudflared.</summary>
public sealed class CloudflaredProcessTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReturnTheExitCode_AndForwardWhatItWrites()
    {
        // "dotnet tunnel --no-autoupdate run" isn't a command dotnet has: it says so and exits with an error.
        List<string> lines = [];

        var exitCode = await new CloudflaredProcess().RunAsync("dotnet", "a-token", line => { lock (lines) { lines.Add(line); } }, Cancel);

        Assert.NotEqual(0, exitCode);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public async Task Should_Throw_WhenThereIsNoSuchProgram()
    {
        var missing = Path.Combine(Path.GetTempPath(), "contactor-tests", "no-cloudflared-here");

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => new CloudflaredProcess().RunAsync(missing, "a-token", _ => { }, Cancel));
    }

    [Fact]
    public async Task Should_Throw_WhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CloudflaredProcess().RunAsync("dotnet", "a-token", _ => { }, cancelled.Token));
    }
}
