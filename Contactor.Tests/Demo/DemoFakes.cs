using Contactor.Demo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Demo;

/// <summary>Starts no process: each run is a <see cref="FakeRun"/> the test ends when it likes.</summary>
internal sealed class FakeStarter : IProgramStarter
{
    private readonly Lock _lock = new();
    private readonly List<FakeRun> _runs = [];

    /// <summary>Whether a new run listens at once; otherwise it waits until the test says, or exits first.</summary>
    public bool Listens { get; set; } = true;

    public IReadOnlyList<FakeRun> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    public IReadOnlyList<FakeRun> RunsOf(string name)
    {
        return [.. Runs.Where(r => r.Start.Name == name)];
    }

    public IRunningProgram Start(ProgramStart start)
    {
        var run = new FakeRun(start, Listens);
        lock (_lock)
        {
            _runs.Add(run);
        }

        return run;
    }
}

internal sealed class FakeRun : IRunningProgram
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRun(ProgramStart start, bool listens)
    {
        Start = start;
        if (listens)
        {
            _listening.TrySetResult();
        }
    }

    public ProgramStart Start { get; }

    /// <summary>Whether the demo stopped it (rather than it exiting by itself).</summary>
    public bool Stopped { get; private set; }

    public bool HasExited => _exited.Task.IsCompleted;

    public Task<int> Exited => _exited.Task;

    public void Listen()
    {
        _listening.TrySetResult();
    }

    /// <summary>The program exits by itself, as the app does after the setup guide's board choice.</summary>
    public void Exit(int code)
    {
        _exited.TrySetResult(code);
    }

    public async Task WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        var first = await Task.WhenAny(_listening.Task, _exited.Task).WaitAsync(cancellationToken);
        if (first == _exited.Task)
        {
            throw new InvalidOperationException("It exited before it listened.");
        }
    }

    public Task StopAsync()
    {
        if (!_exited.Task.IsCompleted)
        {
            Stopped = true;
        }

        _exited.TrySetResult(-1);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>Ports that are only numbers: nothing listens on them in these tests.</summary>
internal sealed class FakePorts : IFreePorts
{
    private int _next = 41000;

    public IReadOnlyList<int> Take(int count)
    {
        return [.. Enumerable.Range(0, count).Select(_ => Interlocked.Increment(ref _next))];
    }
}

/// <summary>Makes no database; remembers the folders it was asked for.</summary>
internal sealed class FakeCopyDatabase : ICopyDatabase
{
    private readonly Lock _lock = new();
    private readonly List<string> _folders = [];

    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_lock)
            {
                return [.. _folders];
            }
        }
    }

    public string ConnectionString(string dataFolder)
    {
        return "Data Source=" + Path.Combine(dataFolder, "contactor.db");
    }

    public Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _folders.Add(dataFolder);
        }

        File.WriteAllText(Path.Combine(dataFolder, "contactor.db"), "");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands in for the app and the simulator behind the proxy: a small web server on the program's port that answers
/// with what it got, and, for a page, the simulator's <c>&lt;base href="/"&gt;</c>.
/// </summary>
internal sealed class EchoStarter : IProgramStarter
{
    public IRunningProgram Start(ProgramStart start)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{start.Port}");
        var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync($"<html><head><base href=\"/\"><title>{start.Name}</title></head><body>{start.Name} page</body></html>");
                return;
            }

            await context.Response.WriteAsync(
                $"{start.Name} {start.Visitor} {context.Request.Path}{context.Request.QueryString} cookie=[{context.Request.Headers.Cookie}]");
        });
        app.StartAsync().GetAwaiter().GetResult();
        return new EchoRun(app);
    }

    private sealed class EchoRun : IRunningProgram
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EchoRun(WebApplication app)
        {
            _app = app;
        }

        public Task<int> Exited => _exited.Task;

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_exited.Task.IsCompleted)
            {
                return;
            }

            await _app.StopAsync();
            _exited.TrySetResult(0);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _app.DisposeAsync();
        }
    }
}

/// <summary>A data root of its own, with stand-ins for the app's and the simulator's .dll, deleted afterwards.</summary>
internal sealed class DemoFolder : IDisposable
{
    public DemoFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "contactor-tests", "demo-" + Guid.NewGuid().ToString("N"));
        DataRoot = Path.Combine(Root, "data");
        AppPath = Path.Combine(Root, "Contactor.Web.dll");
        SimulatorPath = Path.Combine(Root, "Contactor.Simulator.dll");
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(AppPath, "");
        File.WriteAllText(SimulatorPath, "");
    }

    public string Root { get; }

    public string DataRoot { get; }

    public string AppPath { get; }

    public string SimulatorPath { get; }

    public DemoOptions Options(int maxVisitors = 10)
    {
        return new DemoOptions
        {
            AppPath = AppPath,
            SimulatorPath = SimulatorPath,
            DataRoot = DataRoot,
            MaxVisitors = maxVisitors,
        };
    }

    public IReadOnlyList<string> CopyFolders()
    {
        return Directory.Exists(DataRoot) ? Directory.GetDirectories(DataRoot) : [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temp folder; nothing depends on it.
        }
    }
}
