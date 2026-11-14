using System.Collections.Concurrent;
using System.Diagnostics;
using Contactor.Web.Features.Access;

namespace Contactor.Tests.Web;

/// <summary>How a child process (tailscaled, cloudflared) is stopped, with a small shell script standing in for it.</summary>
public sealed class ChildProcessTests
{
    private const string NoSigTerm = "Windows has no SIGTERM: a child process is killed at once there.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LetAProcessStopByItself_WhenItStopsOnSigTerm()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoSigTerm);
        var time = new ManualTimeProvider();
        var script = new Script("trap 'echo stopping; exit 0' TERM; echo ready; while :; do sleep 0.1; done");
        using var stop = new CancellationTokenSource();

        var run = ChildProcess.RunAsync(script.Start, script.Output, time, stop.Token);
        await script.Ready.WaitAsync(Cancel);
        await stop.CancelAsync();

        // The clock never moves, so the wait never ends and nothing kills it: it stopped by itself, on SIGTERM.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Contains("stopping", script.Lines);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_KillAProcess_ThatIgnoresSigTerm_AfterTheWait()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoSigTerm);
        var time = new ManualTimeProvider();
        var script = new Script("trap '' TERM; echo ready; while :; do sleep 0.1; done");
        using var stop = new CancellationTokenSource();

        var run = ChildProcess.RunAsync(script.Start, script.Output, time, stop.Token);
        await script.Ready.WaitAsync(Cancel);
        await stop.CancelAsync();
        for (var tries = 0; !time.Waits(ChildProcess.StopWait); tries++)
        {
            Assert.True(tries < 1000, "It never started waiting for the process to stop.");
            await Task.Delay(10, Cancel);
        }

        Assert.False(run.IsCompleted);
        time.Advance(ChildProcess.StopWait);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["ready"], script.Lines);
        Assert.Equal(0, time.PendingTimers);
    }

    /// <summary>A shell script as the child process, with what it writes.</summary>
    private sealed class Script
    {
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Script(string text)
        {
            Start = new ProcessStartInfo("/bin/sh");
            Start.ArgumentList.Add("-c");
            Start.ArgumentList.Add(text);
        }

        public ProcessStartInfo Start { get; }

        public Task Ready => _ready.Task;

        public string[] Lines => [.. _lines];

        public void Output(string line)
        {
            _lines.Enqueue(line);
            if (line == "ready")
            {
                _ready.TrySetResult();
            }
        }
    }
}
