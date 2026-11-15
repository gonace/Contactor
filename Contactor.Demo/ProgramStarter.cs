using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Contactor.Demo;

/// <summary>
/// A program a copy runs: the app or the simulator, from its .dll, in its own folder (where its settings are), with
/// the environment that sets it up for one visitor, listening on <paramref name="Port"/> on 127.0.0.1.
/// </summary>
/// <param name="Name">"app" or "simulator", for the log.</param>
/// <param name="Visitor">The visitor's short id, for the log (<see cref="VisitorCookie.ForLog"/>).</param>
/// <param name="Environment">The program's whole environment, apart from what <see cref="DotnetProgramStarter"/> keeps.</param>
/// <param name="Probe">How to tell that what listens on the port is this program.</param>
public sealed record ProgramStart(
    string Name,
    string Visitor,
    string DllPath,
    int Port,
    IReadOnlyDictionary<string, string> Environment,
    ProgramProbe Probe);

/// <summary>
/// A request only the program itself answers this way: a GET of <paramref name="Path"/> from 127.0.0.1 that answers
/// 200 with <paramref name="Expect"/> in its body. Anything else on the port, such as a board a visitor's simulator
/// opened there while the program was starting again, isn't it.
/// </summary>
public sealed record ProgramProbe(string Path, string Expect)
{
    /// <summary>The app: its <c>/healthz</c>, which only answers its own machine.</summary>
    public static ProgramProbe App { get; } = new("/healthz", "\"boardConnected\"");

    /// <summary>The simulator: its page.</summary>
    public static ProgramProbe Simulator { get; } = new("/", "<title>Contactor simulator</title>");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const int MaxBody = 64 * 1024;

    /// <summary>Whether what listens on <paramref name="port"/> on 127.0.0.1 answers as the program does.</summary>
    public async Task<bool> AnswersAsync(HttpMessageInvoker client, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{IPAddress.Loopback}:{port}{Path}"));
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            // Read at most a little: whatever listens there may send anything.
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxBody];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, read).Contains(Expect, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }
}

/// <summary>Starts programs. A test puts one in that starts nothing.</summary>
public interface IProgramStarter
{
    IRunningProgram Start(ProgramStart start);
}

/// <summary>One run of a program, until it exits.</summary>
public interface IRunningProgram : IAsyncDisposable
{
    /// <summary>Completes with the exit code when the program exits, by itself or stopped.</summary>
    Task<int> Exited { get; }

    /// <summary>
    /// Completes once the program itself answers on its port (<see cref="ProgramStart.Probe"/>); throws if it exits
    /// first.
    /// </summary>
    Task WaitUntilListeningAsync(CancellationToken cancellationToken);

    /// <summary>Stops the program and everything it started, and waits until it has exited.</summary>
    Task StopAsync();
}

/// <summary>
/// Runs each program as <c>dotnet &lt;dll&gt;</c>, a process of its own, with only what it needs of the demo's
/// environment (<see cref="Kept"/>) and the copy's own settings: nothing else of the demo's reaches a copy.
/// </summary>
public sealed partial class DotnetProgramStarter : IProgramStarter, IDisposable
{
    // Where dotnet and the system are, and the runtime's own settings, by name. A program also reads every DOTNET_
    // variable as a setting (DOTNET_ENVIRONMENT, DOTNET_URLS, DOTNET_Foo__Bar for Foo:Bar), which would make a copy run
    // as something else or listen elsewhere, so no other DOTNET_ variable is passed on, and none with a "__".
    private static readonly string[] Kept =
    [
        "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "SYSTEMROOT", "USERPROFILE",
        "DOTNET_ROOT", "DOTNET_CLI_HOME", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_RUNNING_IN_CONTAINER",
        "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY",
        "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJitForLoops", "DOTNET_ReadyToRun",
        "DOTNET_EnableDiagnostics",
    ];

    // The runtime's own families: where dotnet is for one architecture (DOTNET_ROOT_ARM64) and the garbage collector's
    // knobs (DOTNET_gcServer, DOTNET_GCHeapHardLimit).
    private static readonly string[] KeptPrefixes = ["DOTNET_ROOT_", "DOTNET_GC"];

    private readonly ILogger<DotnetProgramStarter> _logger;

    // For the probes: straight to 127.0.0.1, never through a proxy the demo's environment names.
    private readonly HttpMessageInvoker _probeClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    public DotnetProgramStarter(ILogger<DotnetProgramStarter> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether a variable of the demo's own environment is passed on to a copy's programs.</summary>
    public static bool Keeps(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return !name.Contains("__", StringComparison.Ordinal)
            && (Kept.Contains(name, StringComparer.OrdinalIgnoreCase)
                || KeptPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose()
    {
        _probeClient.Dispose();
    }

    public IRunningProgram Start(ProgramStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(start.DllPath) ?? ".",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        info.ArgumentList.Add(start.DllPath);
        foreach (var name in info.Environment.Keys.ToList())
        {
            if (!Keeps(name))
            {
                info.Environment.Remove(name);
            }
        }

        foreach (var (name, value) in start.Environment)
        {
            info.Environment[name] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Output(start, e.Data);
        process.ErrorDataReceived += (_, e) => Output(start, e.Data);
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        LogStarted(_logger, start.Name, start.Visitor, process.Id, start.Port);
        return new RunningProcess(process, start, _probeClient);
    }

    private void Output(ProgramStart start, string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            LogOutput(_logger, start.Name, start.Visitor, line);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the {Program} of visitor {Visitor}: process {ProcessId}, port {Port}")]
    private static partial void LogStarted(ILogger logger, string program, string visitor, int processId, int port);

    // The copies' own logs, at Debug: the app never logs a board's password, and its sign-in is set, so no setup code.
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Program} of visitor {Visitor}: {Line}")]
    private static partial void LogOutput(ILogger logger, string program, string visitor, string line);

    private sealed class RunningProcess : IRunningProgram
    {
        private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(100);

        private readonly Process _process;
        private readonly ProgramStart _start;
        private readonly HttpMessageInvoker _probeClient;

        public RunningProcess(Process process, ProgramStart start, HttpMessageInvoker probeClient)
        {
            _process = process;
            _start = start;
            _probeClient = probeClient;
            Exited = WaitForExitAsync(process);
        }

        public Task<int> Exited { get; }

        public async Task WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Exited.IsCompleted)
                {
                    throw new InvalidOperationException($"The program exited with code {await Exited} before it listened.");
                }

                // Something answers as the program does, and the program still runs: then it is the program that
                // listens, not something else that took the port while it was starting.
                if (await _start.Probe.AnswersAsync(_probeClient, _start.Port, cancellationToken) && !Exited.IsCompleted)
                {
                    return;
                }

                await Task.WhenAny(Exited, Task.Delay(ProbeEvery, cancellationToken));
            }
        }

        public async Task StopAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // It exited meanwhile.
            }

            await Exited;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _process.Dispose();
        }

        private static async Task<int> WaitForExitAsync(Process process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}

/// <summary>Free ports on 127.0.0.1 for a copy's programs and its simulated board.</summary>
public interface IFreePorts
{
    /// <summary><paramref name="count"/> different ports nothing listens on right now.</summary>
    IReadOnlyList<int> Take(int count);
}

/// <summary>
/// Random ports, so no visitor can tell another's from their own: each one a listener could take a moment ago, all held
/// at once so they differ. Below the system's own range for outgoing connections (from 32768 on Linux).
/// </summary>
public sealed class LoopbackPorts : IFreePorts
{
    public const int First = 20000;
    public const int Last = 32767;

    private const int MaxTries = 1000;

    public IReadOnlyList<int> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        List<TcpListener> listeners = [];
        try
        {
            for (var tries = 0; listeners.Count < count; tries++)
            {
                if (tries == MaxTries)
                {
                    throw new InvalidOperationException($"Found no free port from {First} to {Last}.");
                }

                var listener = new TcpListener(IPAddress.Loopback, RandomNumberGenerator.GetInt32(First, Last + 1));
                try
                {
                    listener.Start();
                    listeners.Add(listener);
                }
                catch (SocketException)
                {
                    // In use: another one.
                    listener.Dispose();
                }
            }

            return [.. listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
                listener.Dispose();
            }
        }
    }
}
