using System.Diagnostics;

namespace Contactor.Web.Features.Access;

/// <summary>Runs a program as a child process of the app, e.g. cloudflared or tailscaled.</summary>
public static class ChildProcess
{
    /// <summary>
    /// Runs <paramref name="start"/> until it exits, passing each line it writes (output and errors) to
    /// <paramref name="output"/>, and returns its exit code. Cancelling stops it and everything it started.
    /// </summary>
    public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(output);
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
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static void Forward(string? line, Action<string> output)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            output(line);
        }
    }
}
