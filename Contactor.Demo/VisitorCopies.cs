using System.Globalization;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

/// <summary>What a page load finds: the visitor's copy, a place kept for one, or no room.</summary>
public enum VisitKind
{
    /// <summary>The visitor's copy runs; the page load counts as activity.</summary>
    Copy,

    /// <summary>
    /// A place is kept for the visitor's id: the copy starts once the page's frames ask for it, so a bot or a link
    /// preview that only reads the page starts nothing.
    /// </summary>
    Kept,

    /// <summary>The demo is full, or stopping.</summary>
    Busy,
}

/// <param name="NewId">The id to give the browser, when it needs a new one.</param>
public readonly record struct Visit(VisitKind Kind, string? NewId = null);

/// <summary>What a request to a copy finds.</summary>
public enum UseKind
{
    /// <summary>The visitor's copy, started now if this was its first request.</summary>
    Copy,

    /// <summary>The visitor's copy was kept, but the demo filled up since.</summary>
    Busy,

    /// <summary>The visitor has no copy: it ended, or never was.</summary>
    None,
}

public readonly record struct Use(UseKind Kind, VisitorCopy? Copy = null);

/// <summary>
/// Every visitor's copy: keeps a place for a new visitor while there is room (<see cref="DemoOptions.MaxVisitors"/>),
/// starts their copy on its first real use, finds it by the id in their cookie, and stops it, with its processes and its
/// data folder, when the visitor ends it or left it (<see cref="DemoOptions.IdleTimeout"/>), when it is too old
/// (<see cref="DemoOptions.MaxLifetime"/>), when one of its programs is given up, or when the demo stops.
/// </summary>
public sealed partial class VisitorCopies : IAsyncDisposable
{
    /// <summary>The address everything of a copy listens on.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>How many ids given to a page and not used yet are kept; the oldest goes first.</summary>
    public const int MaxKept = 1000;

    /// <summary>How long an id given to a page is kept for its frames' first request.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

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

    // Ids given to a page whose copy hasn't started yet, oldest first, with when they were given.
    private readonly OrderedDictionary<string, DateTimeOffset> _kept = new(StringComparer.Ordinal);
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

    /// <summary>How many copies run, the ones still starting included, not the ones stopping.</summary>
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

    /// <summary>How many places are taken: running copies and copies still stopping, whose processes still run.</summary>
    public int Taken
    {
        get
        {
            lock (_lock)
            {
                return TakenLocked;
            }
        }
    }

    /// <summary>How many ids given to a page wait for their copy.</summary>
    public int KeptCount
    {
        get
        {
            lock (_lock)
            {
                return _kept.Count;
            }
        }
    }

    private int TakenLocked => _copies.Count + _stopping.Count;

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
    /// A load of the demo's page by a visitor with this id (null: none). Starts nothing: a new visitor gets a new id,
    /// never one the browser chose, kept until the page's frames use it.
    /// </summary>
    public Visit Visit(string? visitorId)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (visitorId is not null && _copies.TryGetValue(visitorId, out var entry))
            {
                // Loading the page starts the page's count from now, so it counts as activity here too.
                entry.Copy.Active(now);
                return new Visit(VisitKind.Copy);
            }

            if (_closed || TakenLocked >= _options.MaxVisitors)
            {
                return new Visit(VisitKind.Busy);
            }

            if (visitorId is not null && _kept.Remove(visitorId))
            {
                _kept.Add(visitorId, now);
                return new Visit(VisitKind.Kept);
            }

            var id = VisitorCookie.NewId();
            while (_kept.Count >= MaxKept)
            {
                _kept.RemoveAt(0);
            }

            _kept.Add(id, now);
            return new Visit(VisitKind.Kept, id);
        }
    }

    /// <summary>
    /// A request to the visitor's copy: finds it, or starts it on the first request with an id kept for one, while
    /// there is room.
    /// </summary>
    public Use Use(string visitorId)
    {
        Entry entry;
        lock (_lock)
        {
            if (_copies.TryGetValue(visitorId, out var running))
            {
                return new Use(UseKind.Copy, running.Copy);
            }

            if (!_kept.ContainsKey(visitorId))
            {
                return new Use(UseKind.None);
            }

            if (_closed || TakenLocked >= _options.MaxVisitors)
            {
                return new Use(UseKind.Busy);
            }

            _kept.Remove(visitorId);
            entry = Create(visitorId);
            _copies.Add(visitorId, entry);

            // Set before anything can stop it, so a stop always waits for the start to end.
            entry.Starting = StartAsync(entry);
            LogStarting(_logger, entry.Copy.Visitor, TakenLocked, _options.MaxVisitors);
        }

        return new Use(UseKind.Copy, entry.Copy);
    }

    /// <summary>
    /// The page reports a click or a key in either frame. False when the visitor has no copy (any more), nor a place
    /// kept for one whose frames haven't asked yet.
    /// </summary>
    public bool Active(string visitorId)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_copies.TryGetValue(visitorId, out var entry))
            {
                entry.Copy.Active(now);
                return true;
            }

            if (_kept.Remove(visitorId))
            {
                _kept.Add(visitorId, now);
                return true;
            }

            return false;
        }
    }

    /// <summary>The visitor ends their copy (or the page's count down did): stops it and deletes its data.</summary>
    public Task EndAsync(string visitorId)
    {
        lock (_lock)
        {
            _kept.Remove(visitorId);
            return _copies.TryGetValue(visitorId, out var entry) ? StopLocked(entry, "ended on the page") : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stops every copy without a click or a key for <see cref="DemoOptions.IdleTimeout"/> and
    /// <see cref="DemoOptions.IdleWarning"/> together, or older than <see cref="DemoOptions.MaxLifetime"/>, and deletes
    /// its data; forgets ids kept longer than <see cref="KeptFor"/>. Returns how many copies.
    /// </summary>
    public async Task<int> StopExpiredAsync()
    {
        List<Task> stops = [];
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var idle = _options.IdleTimeout + _options.IdleWarning;
            foreach (var entry in _copies.Values.Where(e => e.Copy.HasExpired(now, idle, _options.MaxLifetime)).ToList())
            {
                var reason = now - entry.Copy.StartedAt >= _options.MaxLifetime ? "it reached its lifetime" : "idle";
                stops.Add(StopLocked(entry, reason));
            }

            while (_kept.Count > 0 && now - _kept.GetAt(0).Value >= KeptFor)
            {
                _kept.RemoveAt(0);
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
            _kept.Clear();
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

    private Entry Create(string visitorId)
    {
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
        }, ProgramProbe.App);
        var simulator = new ProgramStart("simulator", visitor, _options.SimulatorPath, simulatorPort, new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = Url(simulatorPort),
            ["ASPNETCORE_ENVIRONMENT"] = _options.CopyEnvironment,
            ["Simulator__Boards__0__Type"] = "vm201",
            ["Simulator__Boards__0__Port"] = board,
            ["Simulator__MaxBoards"] = _options.MaxBoards.ToString(CultureInfo.InvariantCulture),
            ["DOTNET_gcServer"] = "0",
            ["Logging__LogLevel__Default"] = "Warning",
        }, ProgramProbe.Simulator);
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
        // Out of Use's lock before any real work.
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
        var first = await Task.WhenAny(copy.App.GaveUp, copy.Simulator.GaveUp, copy.Stopped);
        if (first != copy.Stopped)
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
        entry.Copy.SetStopped();
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

        public Task Starting { get; set; } = Task.CompletedTask;

        public Task? Stopping { get; set; }
    }
}
