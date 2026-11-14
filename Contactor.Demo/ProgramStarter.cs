using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Contactor.Demo;

/// <summary>
/// A program a copy runs: the app or the simulator, from its .dll, in its own folder (where its settings are), with
/// the environment that sets it up for one visitor, listening on <paramref name="Port"/> on 127.0.0.1.
/// </summary>
/// <param name="Name">"app" or "simulator", for the log.</param>
/// <param name="Visitor">The visitor's short id, for the log (<see cref="VisitorCookie.ForLog"/>).</param>
public sealed record ProgramStart(
    string Name,
    string Visitor,
    string DllPath,
    int Port,
    IReadOnlyDictionary<string, string> Environment);

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

    /// <summary>Completes once the program listens on its port; throws if it exits first.</summary>
    Task WaitUntilListeningAsync(CancellationToken cancellationToken);

    /// <summary>Stops the program and everything it started, and waits until it has exited.</summary>
    Task StopAsync();
}

/// <summary>Runs each program as <c>dotnet &lt;dll&gt;</c>, a process of its own.</summary>
public sealed partial class DotnetProgramStarter : IProgramStarter
{
    // What the demo's own environment may hold that would make a copy listen elsewhere or run as something else.
    private static readonly string[] LeftOut =
    [
        "ASPNETCORE_URLS",
        "ASPNETCORE_HTTP_PORTS",
        "ASPNETCORE_HTTPS_PORTS",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",
        "DOTNET_URLS",
    ];

    private readonly ILogger<DotnetProgramStarter> _logger;

    public DotnetProgramStarter(ILogger<DotnetProgramStarter> logger)
    {
        _logger = logger;
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
            if (LeftOut.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.StartsWith(DemoOptions.SectionName + "__", StringComparison.OrdinalIgnoreCase))
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
        return new RunningProcess(process, start.Port);
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
        private readonly int _port;

        public RunningProcess(Process process, int port)
        {
            _process = process;
            _port = port;
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

                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
                    return;
                }
                catch (SocketException)
                {
                    // Not listening yet.
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

/// <summary>Asks the system: each port is one it gave a listener a moment ago, all held at once so they differ.</summary>
public sealed class LoopbackPorts : IFreePorts
{
    public IReadOnlyList<int> Take(int count)
    {
        List<TcpListener> listeners = [];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
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
