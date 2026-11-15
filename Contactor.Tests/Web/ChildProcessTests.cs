using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Contactor.Web.Features.Access;

namespace Contactor.Tests.Web;

/// <summary>How a child process (tailscaled, cloudflared) is stopped, with a small shell script standing in for it.</summary>
public sealed class ChildProcessTests
{
    private const string NoSigTerm = "Windows has no SIGTERM: a child process is killed at once there.";
    private const string NoProc = "Only Linux lists what a process started (/proc), and the scripts use a POSIX shell.";

    // Only a safety net, so a stop that never ends fails the test instead of hanging it; nothing waits this long.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit, Cancel));
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
        await WaitForTimerAsync(time, ChildProcess.StopWait);

        Assert.False(run.IsCompleted);
        time.Advance(ChildProcess.StopWait);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit, Cancel));
        Assert.Equal(["ready"], script.Lines);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_KillWhatItStarted_WhenItStopsOnSigTerm_AndLeavesThatRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NoProc);
        var time = new ManualTimeProvider();
        var script = new Script("sleep 30 & echo \"leftover $!\"; trap 'exit 0' TERM; echo ready; while :; do sleep 0.1; done");
        using var stop = new CancellationTokenSource();

        var run = ChildProcess.RunAsync(script.Start, script.Output, time, stop.Token);
        try
        {
            await script.Ready.WaitAsync(Cancel);
            await stop.CancelAsync();

            // The clock never moves: it returns because the leftover, which holds its output open, was killed.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit, Cancel));
            await WaitUntilGoneAsync(script.Leftover);
            Assert.Equal(0, time.PendingTimers);
        }
        finally
        {
            KillLeftover(script);
        }
    }

    [Fact]
    public async Task Should_StopReadingItsOutput_AfterTheOutputWait_WhenWhatItStartedOnSigTermHoldsItOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NoProc);
        var time = new ManualTimeProvider();
        var script = new Script("trap 'sleep 30 & echo \"leftover $!\"; exit 0' TERM; echo ready; while :; do sleep 0.1; done");
        using var stop = new CancellationTokenSource();

        var run = ChildProcess.RunAsync(script.Start, script.Output, time, stop.Token);
        try
        {
            await script.Ready.WaitAsync(Cancel);
            await stop.CancelAsync();

            // Started after it was asked to stop, so not known as its own: only the output wait ends the stop.
            await WaitForTimerAsync(time, ChildProcess.OutputWait);
            Assert.False(run.IsCompleted);
            time.Advance(ChildProcess.OutputWait);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit, Cancel));
            Assert.Equal(0, time.PendingTimers);
        }
        finally
        {
            KillLeftover(script);
        }
    }

    [Fact]
    public async Task Should_ReturnTheExitCode_AfterTheOutputWait_WhenWhatItStartedHoldsItsOutputOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), NoProc);
        var time = new ManualTimeProvider();
        var script = new Script("sleep 30 & echo \"leftover $!\"; exit 3");

        var run = ChildProcess.RunAsync(script.Start, script.Output, time, Cancel);
        try
        {
            await WaitForTimerAsync(time, ChildProcess.OutputWait);
            Assert.False(run.IsCompleted);
            time.Advance(ChildProcess.OutputWait);

            Assert.Equal(3, await run.WaitAsync(Limit, Cancel));
            Assert.Equal(0, time.PendingTimers);
        }
        finally
        {
            KillLeftover(script);
        }
    }

    /// <summary>Waits until a timer due <paramref name="dueIn"/> from now runs on <paramref name="time"/>.</summary>
    private static async Task WaitForTimerAsync(ManualTimeProvider time, TimeSpan dueIn)
    {
        for (var tries = 0; !time.Waits(dueIn); tries++)
        {
            Assert.True(tries < 1000, $"It never started waiting {dueIn}.");
            await Task.Delay(10, Cancel);
        }
    }

    /// <summary>Waits until the process <paramref name="id"/> has gone (or is only left to be reaped).</summary>
    private static async Task WaitUntilGoneAsync(Task<int> id)
    {
        var pid = await id.WaitAsync(Limit, Cancel);
        for (var tries = 0; IsRunning(pid); tries++)
        {
            Assert.True(tries < 1000, $"Process {pid} still runs.");
            await Task.Delay(10, Cancel);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Kills the script's leftover if it still runs, so a failing test leaves nothing behind.</summary>
    private static void KillLeftover(Script script)
    {
        if (script.Leftover.IsCompletedSuccessfully && IsRunning(script.Leftover.Result))
        {
            try
            {
                using var leftover = Process.GetProcessById(script.Leftover.Result);
                leftover.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // It has gone in the meantime.
            }
        }
    }

    /// <summary>A shell script as the child process, with what it writes.</summary>
    private sealed class Script
    {
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _leftover = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Script(string text)
        {
            Start = new ProcessStartInfo("/bin/sh");
            Start.ArgumentList.Add("-c");
            Start.ArgumentList.Add(text);
        }

        public ProcessStartInfo Start { get; }

        public Task Ready => _ready.Task;

        /// <summary>The id of the process the script left running, once it has said ("leftover 1234").</summary>
        public Task<int> Leftover => _leftover.Task;

        public string[] Lines => [.. _lines];

        public void Output(string line)
        {
            _lines.Enqueue(line);
            if (line == "ready")
            {
                _ready.TrySetResult();
            }
            else if (line.StartsWith("leftover ", StringComparison.Ordinal)
                && int.TryParse(line["leftover ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                _leftover.TrySetResult(id);
            }
        }
    }
}
