using System.Collections.Concurrent;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        using var service = Create("  ", process, new ManualTimeProvider(), logger);

        await service.StartAsync(Cancel);
        await service.ExecuteTask!;

        Assert.Equal(0, process.Runs);
        Assert.Contains("No tunnel token (Tunnel__Token): cloudflared isn't started", logger.Lines);
    }

    [Fact]
    public async Task Should_RunCloudflared_WithTheTokenAndPath_AndLogItsLines()
    {
        var process = new FakeCloudflared { Output = ["Registered tunnel connection", "2026-11-12T10:00:00Z ERR Failed to dial a quic connection"] };
        var logger = new ListLogger();
        using var service = Create($" {Token} ", process, new ManualTimeProvider(), logger);

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
        using var service = Create(Token, process, time, logger);

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
        using var service = Create(Token, process, time, logger);
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
        using var service = Create(Token, process, time, logger);

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
        using var service = Create(Token, process, new ManualTimeProvider(), new ListLogger());

        await service.StartAsync(Cancel);
        await WaitUntilAsync(() => process.Runs == 1);
        await service.StopAsync(Cancel);

        Assert.True(process.WasCancelled);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // Moves the clock once the service is waiting, so the move can't come before its wait starts.
    private static async Task AdvanceAsync(ManualTimeProvider time, TimeSpan by)
    {
        await WaitUntilAsync(() => time.PendingTimers == 1);
        time.Advance(by);
    }

    // The tunnel as the app runs it: the chosen way in, kept running by AccessHostedService for the app's lifetime.
    private static RunningTunnel Create(string token, FakeCloudflared process, TimeProvider time, ListLogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDbContextFactory<ContactorDbContext>>(new NoDatabase());
        services.AddCloudflare(new ConfigurationBuilder().Build());
        services.AddAccess(new ConfigurationBuilder().Build());
        services.Configure<TunnelOptions>(o =>
        {
            o.Token = token;
            o.Path = "/opt/cloudflared";
        });
        services.AddSingleton(time);
        services.AddSingleton<ICloudflaredProcess>(process);
        services.AddSingleton<ILogger<CloudflareTunnel>>(logger);
        var provider = services.BuildServiceProvider();
        return new RunningTunnel(provider, provider.GetServices<IHostedService>().OfType<AccessHostedService>().Single());
    }

    private sealed class RunningTunnel : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AccessHostedService _service;

        public RunningTunnel(ServiceProvider provider, AccessHostedService service)
        {
            _provider = provider;
            _service = service;
        }

        public Task? ExecuteTask => _service.ExecuteTask;

        public Task StartAsync(CancellationToken cancellationToken) => _service.StartAsync(cancellationToken);

        // As the app stops: it waits only so long, so a service that doesn't stop cloudflared fails a test instead
        // of hanging it.
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(10));
            await _service.StopAsync(stop.Token);
        }

        public void Dispose()
        {
            _service.Dispose();
            _provider.Dispose();
        }
    }

    // The way in is chosen from the database at start; these tests never read it, so the default stays.
    private sealed class NoDatabase : IDbContextFactory<ContactorDbContext>
    {
        public ContactorDbContext CreateDbContext() => throw new InvalidOperationException("No database in these tests.");
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
