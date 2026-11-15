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

/// <summary>Makes no database; remembers the folders it was asked for, and which got the template.</summary>
internal sealed class FakeCopyDatabase : ICopyDatabase
{
    private readonly Lock _lock = new();
    private readonly List<string> _folders = [];
    private readonly List<(string Folder, string TimeZone)> _templates = [];

    /// <summary>What applying the template says: false as for a database the visitor took further.</summary>
    public bool Applies { get; set; } = true;

    /// <summary>Called while the template is applied, e.g. to see that the app doesn't run, with its cancellation.</summary>
    public Func<CancellationToken, Task>? WhileApplying { get; set; }

    /// <summary>The data folders the template was applied to.</summary>
    public IReadOnlyList<string> Templates
    {
        get
        {
            lock (_lock)
            {
                return [.. _templates.Select(t => t.Folder)];
            }
        }
    }

    /// <summary>The time zones the template's days were in, by data folder.</summary>
    public IReadOnlyList<string> TemplateTimeZones
    {
        get
        {
            lock (_lock)
            {
                return [.. _templates.Select(t => t.TimeZone)];
            }
        }
    }

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

    /// <summary>Whether making a database fails, as on a full disk.</summary>
    public bool Fails { get; set; }

    public Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new IOException("disk");
        }

        lock (_lock)
        {
            _folders.Add(dataFolder);
        }

        File.WriteAllText(Path.Combine(dataFolder, "contactor.db"), "");
        return Task.CompletedTask;
    }

    public async Task<bool> ApplyTemplateAsync(string dataFolder, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        if (WhileApplying is { } whileApplying)
        {
            await whileApplying(cancellationToken);
        }

        lock (_lock)
        {
            _templates.Add((dataFolder, timeZone.Id));
        }

        return Applies;
    }
}

/// <summary>
/// Stands in for the app and the simulator behind the proxy: a small web server on the program's port that answers
/// with what it got, and, for a page, the simulator's <c>&lt;base href="/"&gt;</c>.
/// </summary>
internal sealed class EchoStarter : IProgramStarter
{
    private readonly Lock _lock = new();
    private readonly List<ProgramStart> _started = [];
    private readonly List<(string Name, EchoRun Run)> _runs = [];
    private string? _exitsAtOnce;
    private int _requests;

    /// <summary>Whether a program says it listens; otherwise requests wait for it until it is stopped.</summary>
    public bool Listens { get; init; } = true;

    /// <summary>The name of a program that, started from now on, exits at once with 1, before it listens.</summary>
    public string? ExitsAtOnce
    {
        get
        {
            lock (_lock)
            {
                return _exitsAtOnce;
            }
        }
        set
        {
            lock (_lock)
            {
                _exitsAtOnce = value;
            }
        }
    }

    /// <summary>The programs started, in order.</summary>
    public IReadOnlyList<ProgramStart> Started
    {
        get
        {
            lock (_lock)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>How many requests the programs got, all together.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>The program's latest run exits by itself with 1, as one that crashes.</summary>
    public Task ExitAsync(string name)
    {
        EchoRun run;
        lock (_lock)
        {
            run = _runs.Last(r => r.Name == name).Run;
        }

        return run.ExitAsync(1);
    }

    public IRunningProgram Start(ProgramStart start)
    {
        lock (_lock)
        {
            _started.Add(start);
            if (start.Name == _exitsAtOnce)
            {
                return new ExitedRun();
            }
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{start.Port}");
        var app = builder.Build();
        app.Run(async context =>
        {
            Interlocked.Increment(ref _requests);
            if (context.Request.Path == "/redirect")
            {
                // As the app's own redirects come out behind the demo: an absolute address, on the host it was asked
                // for, in the scheme it got (http).
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"http://{context.Request.Host}/setup?step=1#top";
                return;
            }

            if (context.Request.Path == "/redirect-loopback")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"http://127.0.0.1:{start.Port}/setup";
                return;
            }

            if (context.Request.Path == "/redirect-sneaky")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"http://{context.Request.Host}//evil.example/x";
                return;
            }

            if (context.Request.Path == "/redirect-away")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "https://elsewhere.example/x";
                return;
            }

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
        var run = new EchoRun(app, Listens);
        lock (_lock)
        {
            _runs.Add((start.Name, run));
        }

        return run;
    }

    private sealed class EchoRun : IRunningProgram
    {
        private readonly WebApplication _app;
        private readonly bool _listens;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EchoRun(WebApplication app, bool listens)
        {
            _app = app;
            _listens = listens;
        }

        public Task<int> Exited => _exited.Task;

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            return _listens ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public async Task ExitAsync(int code)
        {
            await _app.StopAsync();
            _exited.TrySetResult(code);
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

    // A program that exited before it listened.
    private sealed class ExitedRun : IRunningProgram
    {
        public Task<int> Exited => Task.FromResult(1);

        public Task WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(new InvalidOperationException("It exited before it listened."));
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
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
