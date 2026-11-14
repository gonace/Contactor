using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Contactor.Web.Features.Access;

/// <summary>Runs a program as a child process of the app, e.g. cloudflared or tailscaled.</summary>
public static class ChildProcess
{
    /// <summary>How long a program may take to stop by itself once asked to, before it is killed.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    // SIGTERM is 15 on Linux and macOS alike.
    private const int SigTerm = 15;

    /// <summary>
    /// Runs <paramref name="start"/> until it exits, passing each line it writes (output and errors) to
    /// <paramref name="output"/>, and returns its exit code. Cancelling stops it: on Linux and macOS it is asked to stop
    /// (SIGTERM) and killed, with everything it started, if it hasn't stopped after <see cref="StopWait"/> on
    /// <paramref name="time"/>; on Windows, which has no SIGTERM, it is killed at once.
    /// </summary>
    public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> output, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Forward(e.Data, output);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, output);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, time).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static async Task StopAsync(Process process, TimeProvider time)
    {
        if (!OperatingSystem.IsWindows() && AskToStop(process))
        {
            using var wait = new CancellationTokenSource(StopWait, time);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // It didn't stop in time: kill it below.
            }
        }

        // Does nothing once it has exited, so a process id taken over by another program is never killed.
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Sends SIGTERM to <paramref name="process"/> if it is still running; whether it was sent.</summary>
    private static bool AskToStop(Process process)
    {
        // Only while it hasn't exited: .NET reaps it once it has, and its id may then belong to another program. Like
        // Process.Kill, this leaves the moment between this check and kill(), which .NET can't close either.
        if (process.HasExited)
        {
            return false;
        }

        try
        {
            return Kill(process.Id, SigTerm) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No kill() to call: it is killed at once instead, never left running.
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Kill(int pid, int signal);

    private static void Forward(string? line, Action<string> output)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            output(line);
        }
    }
}
