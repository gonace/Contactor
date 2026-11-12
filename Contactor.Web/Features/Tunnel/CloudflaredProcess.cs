using System.Diagnostics;

namespace Contactor.Web.Features.Tunnel;

/// <summary>Runs cloudflared once, until it exits or is cancelled.</summary>
public interface ICloudflaredProcess
{
    /// <summary>
    /// Runs <c>cloudflared tunnel run</c> with <paramref name="token"/>, passing each line it writes to
    /// <paramref name="output"/>, and returns its exit code. Cancelling stops it.
    /// </summary>
    Task<int> RunAsync(string path, string token, Action<string> output, CancellationToken cancellationToken);
}

/// <summary>cloudflared as a child process of the app.</summary>
public sealed class CloudflaredProcess : ICloudflaredProcess
{
    public async Task<int> RunAsync(string path, string token, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The app updates cloudflared with its own image; it never updates itself.
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("run");

        // Through the environment, so the token never shows in a process list.
        start.Environment["TUNNEL_TOKEN"] = token;

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
