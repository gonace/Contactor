using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Contactor.Web.Features.Access;

/// <summary>Runs a program as a child process of the app, e.g. cloudflared or tailscaled.</summary>
public static class ChildProcess
{
    /// <summary>How long a program may take to stop by itself once asked to, before it is killed.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long, once a program has exited, the app goes on reading what it wrote last. Only something it started that
    /// still runs, and still has its output open, makes it take that long.
    /// </summary>
    public static readonly TimeSpan OutputWait = TimeSpan.FromSeconds(2);

    // SIGKILL is 9 and SIGTERM is 15 on Linux and macOS alike.
    private const int SigKill = 9;
    private const int SigTerm = 15;

    /// <summary>
    /// Runs <paramref name="start"/> until it exits, passing each line it writes (output and errors) to
    /// <paramref name="output"/>, and returns its exit code. Cancelling stops it: on Linux and macOS it is asked to stop
    /// (SIGTERM) and killed, with everything it started, if it hasn't stopped after <see cref="StopWait"/> on
    /// <paramref name="time"/>; on Linux, what it had started when it was asked to stop is killed too if it is still
    /// running once it has stopped. On Windows, which has no SIGTERM, it is killed at once with everything it started.
    /// Once it has exited, what it wrote is read for at most <see cref="OutputWait"/>, so stopping it takes at most
    /// <see cref="StopWait"/> and <see cref="OutputWait"/>, within Docker's 10 s.
    /// </summary>
    public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> output, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        // Raised when the program itself exits. Process.WaitForExitAsync waits for its output to end as well, which
        // something it started can keep open long after it has gone.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => Forward(e.Data, output);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, output);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, exited.Task, time).ConfigureAwait(false);
            throw;
        }

        await ReadLastOutputAsync(process, time).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task StopAsync(Process process, Task exited, TimeProvider time)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Taken before it is asked to stop: once it has exited, what it started is no longer known as its own.
            List<ProcessStamp> started = process.HasExited ? [] : Descendants(process.Id);
            if (AskToStop(process))
            {
                using var wait = new CancellationTokenSource(StopWait, time);
                try
                {
                    await exited.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // It didn't stop in time: kill it below.
                }
            }

            // Does nothing once it has exited, so a process id taken over by another program is never killed.
            process.Kill(entireProcessTree: true);
            KillLeftovers(started);
        }
        else
        {
            process.Kill(entireProcessTree: true);
        }

        await ReadLastOutputAsync(process, time).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits, at most <see cref="OutputWait"/>, for <paramref name="process"/> to exit and its output to end, so its
    /// last lines are passed on. Gives up on output that something it started still holds open.
    /// </summary>
    private static async Task ReadLastOutputAsync(Process process, TimeProvider time)
    {
        using var wait = new CancellationTokenSource(OutputWait, time);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Something it started still has its output open: stop reading it.
        }
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

    /// <summary>A process as /proc shows it: its id and when it started, which together never name another one.</summary>
    private readonly record struct ProcessStamp(int Id, ulong StartedAt);

    /// <summary>
    /// What <paramref name="root"/> started, and what those started, still running; on Linux only, where /proc lists
    /// them. Elsewhere none: there the program is killed with everything it started only if it doesn't stop in time.
    /// </summary>
    private static List<ProcessStamp> Descendants(int root)
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var children = new Dictionary<int, List<ProcessStamp>>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    && ReadStat(id) is { } stat)
                {
                    if (!children.TryGetValue(stat.Parent, out var list))
                    {
                        list = [];
                        children[stat.Parent] = list;
                    }

                    list.Add(new ProcessStamp(id, stat.StartedAt));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // /proc can't be read: what was found so far is all that can be killed later.
        }

        List<ProcessStamp> found = [];
        HashSet<int> seen = [root];
        Queue<int> next = new([root]);
        while (next.TryDequeue(out var parent))
        {
            foreach (var child in children.GetValueOrDefault(parent, []))
            {
                if (seen.Add(child.Id))
                {
                    found.Add(child);
                    next.Enqueue(child.Id);
                }
            }
        }

        return found;
    }

    /// <summary>Kills each of <paramref name="started"/> that still runs; never another program that took its id.</summary>
    private static void KillLeftovers(List<ProcessStamp> started)
    {
        foreach (var leftover in started)
        {
            if (ReadStat(leftover.Id) is { State: not 'Z' } stat && stat.StartedAt == leftover.StartedAt)
            {
                try
                {
                    // A failure means it has exited in the meantime: nothing left to do.
                    _ = Kill(leftover.Id, SigKill);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    // No kill() to call: nothing more can be done.
                    return;
                }
            }
        }
    }

    /// <summary>A process's state, parent and start time from /proc/[id]/stat; null once it is gone.</summary>
    private static (char State, int Parent, ulong StartedAt)? ReadStat(int id)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{id}/stat");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // "id (name) state parent … starttime …": the name may hold spaces and brackets, so fields count from its
        // last ")". The state is field 3, the parent field 4 and the start time field 22.
        var nameEnd = text.LastIndexOf(')');
        if (nameEnd < 0 || nameEnd + 2 >= text.Length)
        {
            return null;
        }

        var fields = text[(nameEnd + 2)..].Split(' ');
        if (fields.Length < 20 || fields[0].Length != 1
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startedAt))
        {
            return null;
        }

        return (fields[0][0], parent, startedAt);
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
