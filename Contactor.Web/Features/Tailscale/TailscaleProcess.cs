using System.Diagnostics;
using System.Text;
using Contactor.Web.Features.Access;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Tailscale;

/// <summary>What a <c>tailscale</c> command said: its exit code and everything it wrote.</summary>
/// <param name="ExitCode">Its exit code; -1 when it was stopped for taking too long.</param>
/// <param name="Output">Its output and errors, a line each.</param>
/// <param name="TimedOut">Whether it was stopped for taking too long.</param>
public sealed record TailscaleCommandResult(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs Tailscale's two programs: tailscaled, its service, and tailscale, the command that talks to it.</summary>
public interface ITailscaleProcess
{
    /// <summary>Runs tailscaled until it exits or is cancelled, passing each line it writes to <paramref name="output"/>.</summary>
    Task<int> RunDaemonAsync(Action<string> output, CancellationToken cancellationToken);

    /// <summary>Runs <c>tailscale</c> with <paramref name="arguments"/>, stopping it after <paramref name="timeout"/>.</summary>
    Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Joins the Tailscale network with <paramref name="authKey"/> as <paramref name="hostName"/>. The key goes to
    /// tailscale in a file of its own, never on its command line, where any process could read it.
    /// </summary>
    Task<TailscaleCommandResult> LogInAsync(string authKey, string hostName, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Tailscale's programs as child processes of the app, as the image has them.</summary>
public sealed class TailscaleProcess : ITailscaleProcess
{
    private readonly TailscaleOptions _options;
    private readonly TimeProvider _time;

    public TailscaleProcess(IOptions<TailscaleOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
    }

    private string Socket => Path.Combine(_options.StateDirectory, "tailscaled.sock");

    public Task<int> RunDaemonAsync(Action<string> output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StateDirectory);
        var start = new ProcessStartInfo(_options.DaemonPath);

        // No root and no network device in the image: Tailscale's own network stack, in the app's process space.
        start.ArgumentList.Add("--tun=userspace-networking");
        start.ArgumentList.Add($"--statedir={_options.StateDirectory}");
        start.ArgumentList.Add($"--socket={Socket}");

        // Its log cache goes to the user's cache folder, which the image's user has none of: keep it with the state.
        start.Environment["XDG_CACHE_HOME"] = _options.StateDirectory;
        return ChildProcess.RunAsync(start, output, cancellationToken);
    }

    public async Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(_options.CliPath);
        start.ArgumentList.Add($"--socket={Socket}");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        void Collect(string line)
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }

        using var limit = new CancellationTokenSource(timeout, _time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            var exitCode = await ChildProcess.RunAsync(start, Collect, both.Token);
            lock (output)
            {
                return new TailscaleCommandResult(exitCode, output.ToString());
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            lock (output)
            {
                return new TailscaleCommandResult(-1, output.ToString(), TimedOut: true);
            }
        }
    }

    public Task<TailscaleCommandResult> LogInAsync(string authKey, string hostName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // --reset: settings not given here go back to their defaults, rather than tailscale refusing to change any.
        return WithKeyFileAsync(_options.StateDirectory, authKey, path =>
            RunAsync(["up", "--reset", $"--auth-key=file:{path}", $"--hostname={hostName}"], timeout, cancellationToken));
    }

    /// <summary>
    /// Writes <paramref name="secret"/> to a new file in <paramref name="directory"/> that only the app's user can
    /// read, gives its path to <paramref name="use"/>, and deletes it afterwards, whatever happens.
    /// </summary>
    public static async Task<T> WithKeyFileAsync<T>(string directory, string secret, Func<string, Task<T>> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"authkey-{Guid.NewGuid():N}");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var file = new FileStream(path, options))
            await using (var writer = new StreamWriter(file))
            {
                await writer.WriteAsync(secret);
            }

            return await use(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
