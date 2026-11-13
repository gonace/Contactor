using System.Diagnostics;
using Contactor.Web.Features.Access;

namespace Contactor.Web.Features.Cloudflare;

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
    public Task<int> RunAsync(string path, string token, Action<string> output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(path);

        // The app updates cloudflared with its own image; it never updates itself.
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("run");

        // Through the environment, so the token never shows in a process list.
        start.Environment["TUNNEL_TOKEN"] = token;
        return ChildProcess.RunAsync(start, output, cancellationToken);
    }
}
