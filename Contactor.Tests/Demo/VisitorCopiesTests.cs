using Contactor.Demo;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Demo;

/// <summary>Each visitor's copy: made while there is room, kept while used, stopped and deleted when idle or at stop.</summary>
public sealed class VisitorCopiesTests : IAsyncDisposable
{
    private readonly DemoFolder _folder = new();
    private readonly FakeStarter _starter = new();
    private readonly FakeCopyDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly VisitorCopies _copies;

    public VisitorCopiesTests()
    {
        _copies = Copies(maxVisitors: 3);
    }

    public async ValueTask DisposeAsync()
    {
        await _copies.DisposeAsync();
        _folder.Dispose();
    }

    [Fact]
    public async Task Should_StartAnAppAndASimulator_OnTheirOwnPorts_PointedAtEachOther()
    {
        var copy = Assert.IsType<VisitorCopy>(_copies.TryStart());
        await copy.Started;

        var app = Assert.Single(_starter.RunsOf("app")).Start;
        var simulator = Assert.Single(_starter.RunsOf("simulator")).Start;
        Assert.Equal(_folder.AppPath, app.DllPath);
        Assert.Equal(_folder.SimulatorPath, simulator.DllPath);
        Assert.NotEqual(app.Port, simulator.Port);
        Assert.Equal($"http://127.0.0.1:{app.Port}", app.Environment["ASPNETCORE_URLS"]);
        Assert.Equal($"http://127.0.0.1:{simulator.Port}", simulator.Environment["ASPNETCORE_URLS"]);

        // The app's VM201 is the visitor's simulated one.
        Assert.Equal("127.0.0.1", app.Environment["Vm201__Host"]);
        Assert.Equal(simulator.Environment["Simulator__Boards__0__Port"], app.Environment["Vm201__Port"]);
        Assert.NotEqual(app.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), app.Environment["Vm201__Port"]);

        // Its database is in the copy's own folder, in the data root, and nothing of the visitor's id is in the path.
        Assert.Equal(_folder.DataRoot, Path.GetDirectoryName(copy.DataFolder));
        Assert.DoesNotContain(copy.VisitorId, copy.DataFolder, StringComparison.Ordinal);
        Assert.Equal([copy.DataFolder], _database.Folders);
        Assert.Contains(copy.DataFolder, app.Environment["ConnectionStrings__Default"], StringComparison.Ordinal);
        Assert.Equal("0", app.Environment["DOTNET_gcServer"]);
        Assert.Equal(copy.Visitor, app.Visitor);
        Assert.Equal(6, copy.Visitor.Length);
    }

    [Fact]
    public async Task Should_StartNoMoreCopies_ThanTheLimit()
    {
        var copies = Enumerable.Range(0, 3).Select(_ => _copies.TryStart()).ToList();
        await Task.WhenAll(copies.Select(c => c!.Started));

        Assert.All(copies, Assert.NotNull);
        Assert.Equal(3, copies.Select(c => c!.VisitorId).Distinct().Count());
        Assert.Null(_copies.TryStart());
        Assert.Equal(6, _starter.Runs.Count);

        // Room again once one is gone.
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(3, await _copies.StopIdleAsync());
        Assert.NotNull(_copies.TryStart());
    }

    [Fact]
    public async Task Should_StopACopy_After30MinutesWithoutUse_AndDeleteItsData()
    {
        var copy = _copies.TryStart()!;
        await copy.Started;
        Assert.Single(_folder.CopyFolders());

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(_copies.Touch(copy.VisitorId));
        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, await _copies.StopIdleAsync());
        Assert.All(_starter.Runs, r => Assert.False(r.HasExited));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _copies.StopIdleAsync());

        Assert.All(_starter.Runs, r => Assert.True(r.Stopped));
        Assert.Empty(_folder.CopyFolders());
        Assert.Equal(0, _copies.Count);
        Assert.False(_copies.Touch(copy.VisitorId));
        Assert.Null(_copies.Begin(copy.VisitorId));
    }

    [Fact]
    public async Task Should_KeepACopy_WhileAConnectionIsOpen()
    {
        var copy = _copies.TryStart()!;
        await copy.Started;

        // Blazor's live connection is one long request.
        Assert.Same(copy, _copies.Begin(copy.VisitorId));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await _copies.StopIdleAsync());

        _copies.End(copy);
        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, await _copies.StopIdleAsync());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _copies.StopIdleAsync());
    }

    [Fact]
    public async Task Should_LookForIdleCopies_EveryMinute()
    {
        var copy = _copies.TryStart()!;
        await copy.Started;
        using var service = new DemoHostedService(_copies, _time);
        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var minute = 1; minute <= 29; minute++)
        {
            await Wait.UntilAsync(() => _time.Waits(DemoHostedService.SweepEvery));
            _time.Advance(DemoHostedService.SweepEvery);
        }

        await Wait.UntilAsync(() => _time.Waits(DemoHostedService.SweepEvery));
        Assert.Equal(1, _copies.Count);
        _time.Advance(DemoHostedService.SweepEvery);

        await Wait.UntilAsync(() => _copies.Count == 0 && _folder.CopyFolders().Count == 0);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Should_StopEveryCopy_WhenTheDemoStops_AndStartNoMore()
    {
        var copies = new[] { _copies.TryStart()!, _copies.TryStart()! };
        await Task.WhenAll(copies.Select(c => c.Started));
        using var service = new DemoHostedService(_copies, _time);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, _starter.Runs.Count);
        Assert.All(_starter.Runs, r => Assert.True(r.Stopped));
        Assert.Empty(_folder.CopyFolders());
        Assert.Null(_copies.TryStart());
    }

    [Fact]
    public async Task Should_StopACopy_StoppedWhileItsDatabaseIsMade_WithoutStartingIt()
    {
        var database = new GatedCopyDatabase();
        await using var copies = Copies(maxVisitors: 3, database);
        var copy = copies.TryStart()!;
        await database.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stop = copies.StopAllAsync();
        database.Release.TrySetResult();
        await stop;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.Started);
        Assert.Empty(_starter.Runs);
        Assert.Empty(_folder.CopyFolders());
    }

    [Fact]
    public async Task Should_StopACopy_WhoseProgramIsGivenUp()
    {
        _starter.Listens = false;
        var copy = _copies.TryStart()!;
        await copy.Started;

        for (var i = 1; i <= SupervisedProgram.MaxFailedStarts; i++)
        {
            await Wait.UntilAsync(() => _starter.RunsOf("app").Count == i);
            _starter.RunsOf("app")[^1].Exit(1);
        }

        await Wait.UntilAsync(() => _copies.Count == 0 && _folder.CopyFolders().Count == 0);
        Assert.All(_starter.RunsOf("simulator"), r => Assert.True(r.Stopped));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => copy.App.Listening);
    }

    [Fact]
    public async Task Should_DeleteOnlyCopies_LeftInTheDataRoot()
    {
        var left = Path.Combine(_folder.DataRoot, VisitorCookie.NewId());
        var other = Path.Combine(_folder.DataRoot, "keep-me");
        Directory.CreateDirectory(Path.Combine(left, "keys"));
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(_folder.DataRoot, "note.txt"), "", TestContext.Current.CancellationToken);

        _copies.CleanDataRoot();

        Assert.False(Directory.Exists(left));
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(Path.Combine(_folder.DataRoot, "note.txt")));
    }

    private VisitorCopies Copies(int maxVisitors, ICopyDatabase? database = null)
    {
        return new VisitorCopies(
            Options.Create(_folder.Options(maxVisitors)),
            _starter,
            new FakePorts(),
            database ?? _database,
            _time,
            NullLoggerFactory.Instance);
    }

    private sealed class GatedCopyDatabase : ICopyDatabase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectionString(string dataFolder)
        {
            return "Data Source=" + Path.Combine(dataFolder, "contactor.db");
        }

        public async Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            File.WriteAllText(Path.Combine(dataFolder, "contactor.db"), "");
        }
    }
}
