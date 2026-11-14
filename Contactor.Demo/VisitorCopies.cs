using System.Globalization;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

/// <summary>
/// Every visitor's copy: makes one for a new visitor while there is room (<see cref="DemoOptions.MaxVisitors"/>),
/// finds a visitor's own by the id in their cookie, and stops a copy, with its processes and its data folder, when it
/// has been idle too long, when one of its programs is given up, or when the demo stops.
/// </summary>
public sealed partial class VisitorCopies : IAsyncDisposable
{
    /// <summary>The address everything of a copy listens on.</summary>
    public const string Host = "127.0.0.1";

    private readonly DemoOptions _options;
    private readonly IProgramStarter _starter;
    private readonly IFreePorts _ports;
    private readonly ICopyDatabase _database;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<VisitorCopies> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _copies = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _stopping = [];
    private bool _closed;

    public VisitorCopies(
        IOptions<DemoOptions> options,
        IProgramStarter starter,
        IFreePorts ports,
        ICopyDatabase database,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);
        _options = options.Value;
        _starter = starter;
        _ports = ports;
        _database = database;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<VisitorCopies>();
    }

    /// <summary>How many copies there are, the ones still starting included.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _copies.Count;
            }
        }
    }

    /// <summary>
    /// Deletes what copies left in <see cref="DemoOptions.DataRoot"/> when the demo didn't stop cleanly: only folders
    /// named as the demo names a copy's, so nothing else there is touched.
    /// </summary>
    public void CleanDataRoot()
    {
        Directory.CreateDirectory(_options.DataRoot);
        foreach (var folder in Directory.EnumerateDirectories(_options.DataRoot))
        {
            if (VisitorCookie.IsWellFormed(Path.GetFileName(folder)))
            {
                DeleteFolder(folder);
            }
        }
    }

    /// <summary>
    /// A new visitor's copy, with a new id for their cookie, starting in the background; null while the demo is full
    /// or stopping.
    /// </summary>
    public VisitorCopy? TryStart()
    {
        Entry entry;
        lock (_lock)
        {
            if (_closed || _copies.Count >= _options.MaxVisitors)
            {
                return null;
            }

            entry = Create();
            _copies.Add(entry.Copy.VisitorId, entry);

            // Set before anything can stop it, so a stop always waits for the start to end.
            entry.Starting = StartAsync(entry);
            LogStarting(_logger, entry.Copy.Visitor, _copies.Count, _options.MaxVisitors);
        }

        return entry.Copy;
    }

    /// <summary>A page load of a visitor whose copy is still there: counts as use. False when it isn't there.</summary>
    public bool Touch(string visitorId)
    {
        lock (_lock)
        {
            if (!_copies.TryGetValue(visitorId, out var entry))
            {
                return false;
            }

            entry.Copy.Touch(_time.GetUtcNow());
            return true;
        }
    }

    /// <summary>
    /// A request or connection to the visitor's copy begins: it isn't idle until <see cref="End"/>. Null when the
    /// visitor has no copy (stopped, or never had one).
    /// </summary>
    public VisitorCopy? Begin(string visitorId)
    {
        lock (_lock)
        {
            if (!_copies.TryGetValue(visitorId, out var entry))
            {
                return null;
            }

            entry.Copy.Begin(_time.GetUtcNow());
            return entry.Copy;
        }
    }

    public void End(VisitorCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        lock (_lock)
        {
            copy.End(_time.GetUtcNow());
        }
    }

    /// <summary>Stops every copy idle for <see cref="DemoOptions.IdleTimeout"/> and deletes its data. Returns how many.</summary>
    public async Task<int> StopIdleAsync()
    {
        List<Task> stops = [];
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var entry in _copies.Values.Where(e => e.Copy.IsIdle(now, _options.IdleTimeout)).ToList())
            {
                stops.Add(StopLocked(entry, "idle"));
            }
        }

        await Task.WhenAll(stops);
        return stops.Count;
    }

    /// <summary>Stops every copy and deletes its data; no new copy starts after this.</summary>
    public async Task StopAllAsync()
    {
        List<Task> stops;
        lock (_lock)
        {
            _closed = true;
            foreach (var entry in _copies.Values.ToList())
            {
                StopLocked(entry, "the demo stops");
            }

            stops = [.. _stopping.Select(e => e.Stopping).OfType<Task>()];
        }

        await Task.WhenAll(stops);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }

    private Entry Create()
    {
        var visitorId = VisitorCookie.NewId();
        var visitor = VisitorCookie.ForLog(visitorId);

        // The folder has an id of its own: nothing a browser sends ever becomes part of a path.
        var dataFolder = Path.Combine(_options.DataRoot, VisitorCookie.NewId());
        var ports = _ports.Take(3);
        var (appPort, simulatorPort, boardPort) = (ports[0], ports[1], ports[2]);
        var board = boardPort.ToString(CultureInfo.InvariantCulture);
        var app = new ProgramStart("app", visitor, _options.AppPath, appPort, new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = Url(appPort),
            ["ASPNETCORE_ENVIRONMENT"] = _options.CopyEnvironment,
            ["ConnectionStrings__Default"] = _database.ConnectionString(dataFolder),
            ["Vm201__Host"] = Host,
            ["Vm201__Port"] = board,
            ["DOTNET_gcServer"] = "0",
            ["Logging__LogLevel__Default"] = "Warning",
        });
        var simulator = new ProgramStart("simulator", visitor, _options.SimulatorPath, simulatorPort, new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = Url(simulatorPort),
            ["ASPNETCORE_ENVIRONMENT"] = _options.CopyEnvironment,
            ["Simulator__Boards__0__Type"] = "vm201",
            ["Simulator__Boards__0__Port"] = board,
            ["DOTNET_gcServer"] = "0",
            ["Logging__LogLevel__Default"] = "Warning",
        });
        var copy = new VisitorCopy(
            visitorId,
            dataFolder,
            Supervise(app),
            Supervise(simulator),
            _time.GetUtcNow());
        return new Entry(copy);
    }

    private SupervisedProgram Supervise(ProgramStart start)
    {
        return new SupervisedProgram(start, _starter, _options.StartTimeout, _time, _loggers.CreateLogger<SupervisedProgram>());
    }

    private static string Url(int port)
    {
        return string.Create(CultureInfo.InvariantCulture, $"http://{Host}:{port}");
    }

    private async Task StartAsync(Entry entry)
    {
        // Out of TryStart's lock before any real work.
        await Task.Yield();
        var copy = entry.Copy;
        try
        {
            Directory.CreateDirectory(copy.DataFolder);
            await _database.PrepareAsync(copy.DataFolder, entry.Cancel.Token);
            entry.Cancel.Token.ThrowIfCancellationRequested();
            copy.Simulator.Start();
            copy.App.Start();
            copy.SetStarted();
        }
        catch (Exception ex)
        {
            copy.SetFailed(ex);
            if (ex is not OperationCanceledException)
            {
                LogStartFailed(_logger, copy.Visitor, ex);
            }

            // Not awaited: the stop waits for this start to end.
            _ = Stop(entry, "it didn't start");
            return;
        }

        _ = WatchAsync(entry);
    }

    private async Task WatchAsync(Entry entry)
    {
        var copy = entry.Copy;
        var first = await Task.WhenAny(copy.App.GaveUp, copy.Simulator.GaveUp, entry.Stopped.Task);
        if (first != entry.Stopped.Task)
        {
            await Stop(entry, "a program was given up");
        }
    }

    private Task Stop(Entry entry, string reason)
    {
        lock (_lock)
        {
            return StopLocked(entry, reason);
        }
    }

    // Under the lock: takes the copy out (so no new request reaches it) and stops it once. Returns that stop.
    private Task StopLocked(Entry entry, string reason)
    {
        if (entry.Stopping is { } stopping)
        {
            return stopping;
        }

        _copies.Remove(entry.Copy.VisitorId);
        _stopping.Add(entry);
        entry.Stopped.TrySetResult();
        entry.Cancel.Cancel();
        entry.Stopping = StopAsync(entry, reason);
        return entry.Stopping;
    }

    private async Task StopAsync(Entry entry, string reason)
    {
        // Lets StopLocked finish under its lock first.
        await Task.Yield();
        var copy = entry.Copy;
        try
        {
            try
            {
                await entry.Starting;
            }
            catch (Exception)
            {
                // Its failure was logged where it happened; what it started is stopped below all the same.
            }

            await Task.WhenAll(copy.App.DisposeAsync().AsTask(), copy.Simulator.DisposeAsync().AsTask());
            DeleteFolder(copy.DataFolder);
            LogStopped(_logger, copy.Visitor, reason);
        }
        catch (Exception ex)
        {
            LogStopFailed(_logger, copy.Visitor, ex);
        }
        finally
        {
            entry.Cancel.Dispose();
            lock (_lock)
            {
                _stopping.Remove(entry);
            }
        }
    }

    private void DeleteFolder(string folder)
    {
        // Only ever a folder directly in the data root.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.DataRoot));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Won't delete {folder}: it isn't in {root}.");
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(_logger, folder, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting a copy for visitor {Visitor} ({Count} of {Max})")]
    private static partial void LogStarting(ILogger logger, string visitor, int count, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped the copy of visitor {Visitor} and deleted its data: {Reason}")]
    private static partial void LogStopped(ILogger logger, string visitor, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The copy of visitor {Visitor} didn't start")]
    private static partial void LogStartFailed(ILogger logger, string visitor, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The copy of visitor {Visitor} didn't stop cleanly")]
    private static partial void LogStopFailed(ILogger logger, string visitor, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete {Folder}")]
    private static partial void LogDeleteFailed(ILogger logger, string folder, Exception exception);

    private sealed class Entry
    {
        public Entry(VisitorCopy copy)
        {
            Copy = copy;
        }

        public VisitorCopy Copy { get; }

        public CancellationTokenSource Cancel { get; } = new();

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Starting { get; set; } = Task.CompletedTask;

        public Task? Stopping { get; set; }
    }
}
