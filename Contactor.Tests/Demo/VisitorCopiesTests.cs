using Contactor.Demo;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Demo;

/// <summary>
/// Each visitor's copy: a place kept by the page, started by its frames while there is room, kept while the visitor
/// clicks, and stopped and deleted when idle, too old, ended, or at stop.
/// </summary>
public sealed class VisitorCopiesTests : IAsyncDisposable
{
    private static readonly TimeSpan IdleAndWarning = TimeSpan.FromMinutes(17);

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
        var copy = Start(_copies);
        await copy.Started;

        var app = Assert.Single(_starter.RunsOf("app")).Start;
        var simulator = Assert.Single(_starter.RunsOf("simulator")).Start;
        Assert.Equal(_folder.AppPath, app.DllPath);
        Assert.Equal(_folder.SimulatorPath, simulator.DllPath);
        Assert.NotEqual(app.Port, simulator.Port);
        Assert.Equal($"http://127.0.0.1:{app.Port}", app.Environment["ASPNETCORE_URLS"]);
        Assert.Equal($"http://127.0.0.1:{simulator.Port}", simulator.Environment["ASPNETCORE_URLS"]);
        Assert.Same(ProgramProbe.App, app.Probe);
        Assert.Same(ProgramProbe.Simulator, simulator.Probe);

        // The app's VM201 is the visitor's simulated one, and the visitor has at most 20 boards.
        Assert.Equal("127.0.0.1", app.Environment["Vm201__Host"]);
        Assert.Equal(simulator.Environment["Simulator__Boards__0__Port"], app.Environment["Vm201__Port"]);
        Assert.NotEqual(app.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), app.Environment["Vm201__Port"]);
        Assert.Equal("20", simulator.Environment["Simulator__MaxBoards"]);

        // Its database is in the copy's own folder, in the data root, and nothing of the visitor's id is in the path.
        Assert.Equal(_folder.DataRoot, Path.GetDirectoryName(copy.DataFolder));
        Assert.DoesNotContain(copy.VisitorId, copy.DataFolder, StringComparison.Ordinal);
        Assert.Equal([copy.DataFolder], _database.Folders);
        Assert.Contains(copy.DataFolder, app.Environment["ConnectionStrings__Default"], StringComparison.Ordinal);
        Assert.Equal("0", app.Environment["DOTNET_gcServer"]);
        Assert.Equal("UTC", app.Environment["TimeZone"]);
        Assert.Equal(copy.Visitor, app.Visitor);
        Assert.Equal(6, copy.Visitor.Length);
    }

    [Fact]
    public async Task Should_RunTheCopy_AndItsTemplate_InTheVisitorsTimeZone_FromItsStartToItsEnd()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var id = _copies.Visit(null).NewId!;
        var copy = _copies.Use(id, newYork).Copy!;
        await copy.Started;

        // The frames' later requests, also from a browser that moved meanwhile, find the copy as it is.
        Assert.Same(copy, _copies.Use(id, TimeZoneInfo.Utc).Copy);
        Assert.Equal(TemplateResult.Applied, await _copies.ApplyTemplateAsync(id));

        Assert.Equal("America/New_York", copy.TimeZone.Id);
        Assert.All(_starter.RunsOf("app"), r => Assert.Equal("America/New_York", r.Start.Environment["TimeZone"]));
        Assert.Equal(["America/New_York"], _database.TemplateTimeZones);
    }

    [Fact]
    public void Should_StartNothing_WhenOnlyThePageIsRead()
    {
        // A bot or a link preview reads the page many times, and never asks for its frames.
        for (var i = 0; i < 50; i++)
        {
            var visit = _copies.Visit(null);
            Assert.Equal(VisitKind.Kept, visit.Kind);
            Assert.True(VisitorCookie.IsWellFormed(visit.NewId));
        }

        Assert.Empty(_starter.Runs);
        Assert.Equal(0, _copies.Taken);
        Assert.Equal(50, _copies.KeptCount);
        Assert.Empty(_folder.CopyFolders());
    }

    [Fact]
    public async Task Should_StartTheCopy_OnTheFramesFirstRequest_Once()
    {
        var id = _copies.Visit(null).NewId!;

        // Both frames ask at once; they get the one copy.
        var app = _copies.Use(id);
        var simulator = _copies.Use(id);

        Assert.Equal(UseKind.Copy, app.Kind);
        Assert.Same(app.Copy, simulator.Copy);
        Assert.Equal(id, app.Copy!.VisitorId);
        await app.Copy.Started;
        Assert.Equal(2, _starter.Runs.Count);
        Assert.Equal(0, _copies.KeptCount);

        // The page again: the copy, and no new id.
        Assert.Equal(new Visit(VisitKind.Copy, OffersTemplate: true), _copies.Visit(id));
    }

    [Fact]
    public async Task Should_KeepThePlace_WhenThePageReportsAClickBeforeItsFrames()
    {
        var id = _copies.Visit(null).NewId!;
        _time.Advance(VisitorCopies.KeptFor - TimeSpan.FromMinutes(1));

        Assert.True(_copies.Active(id));
        _time.Advance(TimeSpan.FromMinutes(2));
        await _copies.StopExpiredAsync();

        Assert.Equal(UseKind.Copy, _copies.Use(id).Kind);
        Assert.False(_copies.Active("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void Should_KeepTheSameId_ForAPageLoadedAgainBeforeItsFrames()
    {
        var id = _copies.Visit(null).NewId!;

        Assert.Equal(new Visit(VisitKind.Kept, OffersTemplate: true), _copies.Visit(id));
        Assert.Equal(1, _copies.KeptCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void Should_StartNoCopy_ForAnIdTheDemoDidntGive(string? given)
    {
        var visit = _copies.Visit(given);

        Assert.NotEqual(given, visit.NewId);
        Assert.Equal(UseKind.None, _copies.Use("0123456789abcdef0123456789abcdef").Kind);
        Assert.Empty(_starter.Runs);
    }

    [Fact]
    public async Task Should_ForgetKeptIds_AfterAWhile_AndTheOldestFirstWhenThereAreTooMany()
    {
        var first = _copies.Visit(null).NewId!;
        for (var i = 1; i < VisitorCopies.MaxKept; i++)
        {
            _copies.Visit(null);
        }

        Assert.Equal(VisitorCopies.MaxKept, _copies.KeptCount);
        var newest = _copies.Visit(null).NewId!;
        Assert.Equal(VisitorCopies.MaxKept, _copies.KeptCount);
        Assert.Equal(UseKind.None, _copies.Use(first).Kind);

        _time.Advance(VisitorCopies.KeptFor);
        Assert.Equal(0, await _copies.StopExpiredAsync());
        Assert.Equal(0, _copies.KeptCount);
        Assert.Equal(UseKind.None, _copies.Use(newest).Kind);
    }

    [Fact]
    public async Task Should_StartNoMoreCopies_ThanTheLimit()
    {
        var copies = Enumerable.Range(0, 3).Select(_ => Start(_copies)).ToList();
        await Task.WhenAll(copies.Select(c => c.Started));

        Assert.Equal(3, copies.Select(c => c.VisitorId).Distinct().Count());
        Assert.Equal(VisitKind.Busy, _copies.Visit(null).Kind);
        Assert.Equal(6, _starter.Runs.Count);

        // Room again once one is gone.
        _time.Advance(IdleAndWarning);
        Assert.Equal(3, await _copies.StopExpiredAsync());
        Assert.Equal(VisitKind.Kept, _copies.Visit(null).Kind);
    }

    [Fact]
    public async Task Should_NotStartAKeptCopy_WhenTheDemoFilledUpSince()
    {
        var kept = _copies.Visit(null).NewId!;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Start(_copies).Started));

        Assert.Equal(UseKind.Busy, _copies.Use(kept).Kind);
        Assert.Equal(6, _starter.Runs.Count);
    }

    [Fact]
    public async Task Should_CountCopiesStillStopping_AsTaken()
    {
        var database = new GatedCopyDatabase();
        await using var copies = Copies(maxVisitors: 1, database);
        var copy = Start(copies);
        await database.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Its stop waits for its start; its processes would still run.
        var end = copies.EndAsync(copy.VisitorId);
        Assert.Equal(0, copies.Count);
        Assert.Equal(1, copies.Taken);
        Assert.Equal(VisitKind.Busy, copies.Visit(null).Kind);

        database.Release.TrySetResult();
        await end;
        Assert.Equal(0, copies.Taken);
        Assert.Equal(VisitKind.Kept, copies.Visit(null).Kind);
    }

    [Fact]
    public async Task Should_KeepACopy_WhileTheVisitorClicks()
    {
        var copy = Start(_copies);
        await copy.Started;

        for (var i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(16));
            Assert.True(_copies.Active(copy.VisitorId));
            Assert.Equal(0, await _copies.StopExpiredAsync());
        }

        Assert.All(_starter.Runs, r => Assert.False(r.HasExited));
    }

    [Fact]
    public async Task Should_StopACopy_After15And2MinutesWithoutAClick_AndDeleteItsData()
    {
        var copy = Start(_copies);
        await copy.Started;
        Assert.Single(_folder.CopyFolders());

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(_copies.Active(copy.VisitorId));
        _time.Advance(IdleAndWarning - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await _copies.StopExpiredAsync());
        Assert.All(_starter.Runs, r => Assert.False(r.HasExited));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await _copies.StopExpiredAsync());

        Assert.All(_starter.Runs, r => Assert.True(r.Stopped));
        Assert.Empty(_folder.CopyFolders());
        Assert.Equal(0, _copies.Count);
        Assert.True(copy.Stopped.IsCompleted);
        Assert.False(_copies.Active(copy.VisitorId));
        Assert.Equal(UseKind.None, _copies.Use(copy.VisitorId).Kind);
    }

    [Fact]
    public async Task Should_CountAPageLoad_AsActivity()
    {
        var copy = Start(_copies);
        await copy.Started;

        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(VisitKind.Copy, _copies.Visit(copy.VisitorId).Kind);
        _time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(0, await _copies.StopExpiredAsync());
    }

    [Fact]
    public async Task Should_StopACopy_After2Hours_EvenWhileTheVisitorClicks()
    {
        var copy = Start(_copies);
        await copy.Started;

        for (var minute = 10; minute < 120; minute += 10)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            Assert.True(_copies.Active(copy.VisitorId));
            Assert.Equal(0, await _copies.StopExpiredAsync());
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(_copies.Active(copy.VisitorId));
        Assert.Equal(1, await _copies.StopExpiredAsync());
        Assert.Empty(_folder.CopyFolders());
    }

    [Fact]
    public async Task Should_EndOnlyTheVisitorsOwnCopy()
    {
        var mine = Start(_copies);
        var theirs = Start(_copies);
        await Task.WhenAll(mine.Started, theirs.Started);

        await _copies.EndAsync(mine.VisitorId);
        await _copies.EndAsync("0123456789abcdef0123456789abcdef");

        Assert.True(mine.Stopped.IsCompleted);
        Assert.False(theirs.Stopped.IsCompleted);
        Assert.Equal(1, _copies.Count);
        Assert.Equal(VisitKind.Copy, _copies.Visit(theirs.VisitorId).Kind);
    }

    [Fact]
    public async Task Should_KeepTheCopy_WhenOneTabsCountDownRunsOut_WhileAnotherTabIsUsed()
    {
        var copy = Start(_copies);
        await copy.Started;

        // Tab A is left alone; tab B, with the same cookie, reports a click a minute before A's count down ends.
        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.True(_copies.Active(copy.VisitorId));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await _copies.EndIfIdleAsync(copy.VisitorId));
        Assert.False(copy.Stopped.IsCompleted);
        Assert.Equal(1, _copies.Count);
    }

    [Fact]
    public async Task Should_KeepTheCopy_ForAClickJustWithinTheIdleTimeout_ReportedAsLateAsAPageMay()
    {
        var copy = Start(_copies);
        await copy.Started;
        var reportEvery = DemoPages.ReportEvery(_folder.Options());
        Assert.Equal(TimeSpan.FromMinutes(1), reportEvery);

        // Tab B reports a click; its next click, a moment later, waits for the report a minute after the first.
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_copies.Active(copy.VisitorId));
        _time.Advance(reportEvery);
        Assert.True(_copies.Active(copy.VisitorId));

        // Tab A's count down runs out when B's last click is a moment short of the idle timeout.
        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.False(await _copies.EndIfIdleAsync(copy.VisitorId));

        // Nothing from any tab for the idle timeout: the count down ends it.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await _copies.EndIfIdleAsync(copy.VisitorId));
        Assert.True(copy.Stopped.IsCompleted);
        Assert.Equal(0, _copies.Taken);
        Assert.Empty(_folder.CopyFolders());
    }

    [Fact]
    public async Task Should_EndTheCopy_WhenTheOnlyTabsCountDownRunsOut_HeardOfAsLateAsAPageMay()
    {
        var copy = Start(_copies);
        await copy.Started;
        var reportEvery = DemoPages.ReportEvery(_folder.Options());

        // The tab's last click was reported as late as it may be: its count down runs out sooner, by the demo's clock.
        _time.Advance(reportEvery);
        Assert.True(_copies.Active(copy.VisitorId));
        _time.Advance(IdleAndWarning - reportEvery);

        Assert.True(await _copies.EndIfIdleAsync(copy.VisitorId));
        Assert.True(copy.Stopped.IsCompleted);
        Assert.Equal(0, _copies.Count);
    }

    [Fact]
    public async Task Should_EndTheCopy_OnStartAgain_EvenJustAfterAClick()
    {
        var copy = Start(_copies);
        await copy.Started;
        Assert.True(_copies.Active(copy.VisitorId));

        await _copies.EndAsync(copy.VisitorId);

        Assert.True(copy.Stopped.IsCompleted);
        Assert.Equal(0, _copies.Taken);
    }

    [Fact]
    public async Task Should_AnswerACountDown_ForAVisitorWithoutACopy()
    {
        // A place kept by a page load within the idle timeout stays kept; one older than that goes.
        var visit = _copies.Visit(null);
        var id = visit.NewId!;
        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(await _copies.EndIfIdleAsync(id));
        Assert.Equal(1, _copies.KeptCount);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await _copies.EndIfIdleAsync(id));
        Assert.Equal(0, _copies.KeptCount);
        Assert.Equal(UseKind.None, _copies.Use(id).Kind);

        // Nothing at all: nothing is left.
        Assert.True(await _copies.EndIfIdleAsync("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public async Task Should_LookForIdleCopies_EveryMinute()
    {
        var copy = Start(_copies);
        await copy.Started;
        using var service = new DemoHostedService(_copies, _time);
        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var minute = 1; minute <= 16; minute++)
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
        var copies = new[] { Start(_copies), Start(_copies) };
        await Task.WhenAll(copies.Select(c => c.Started));
        var kept = _copies.Visit(null).NewId!;
        using var service = new DemoHostedService(_copies, _time);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, _starter.Runs.Count);
        Assert.All(_starter.Runs, r => Assert.True(r.Stopped));
        Assert.Empty(_folder.CopyFolders());
        Assert.Equal(VisitKind.Busy, _copies.Visit(null).Kind);
        Assert.Equal(UseKind.None, _copies.Use(kept).Kind);
    }

    [Fact]
    public async Task Should_StopACopy_StoppedWhileItsDatabaseIsMade_WithoutStartingIt()
    {
        var database = new GatedCopyDatabase();
        await using var copies = Copies(maxVisitors: 3, database);
        var copy = Start(copies);
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
        var copy = Start(_copies);
        await copy.Started;

        for (var i = 1; i <= SupervisedProgram.MaxFailedStarts; i++)
        {
            await Wait.UntilAsync(() => _starter.RunsOf("app").Count == i);
            _starter.RunsOf("app")[^1].Exit(1);
        }

        await Wait.UntilAsync(() => _copies.Taken == 0 && _folder.CopyFolders().Count == 0);
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

    [Fact]
    public async Task Should_ApplyTheTemplate_WithTheAppStopped_AndStartItAgain()
    {
        var copy = Start(_copies);
        await copy.Started;
        await copy.App.Listening;
        var first = Assert.Single(_starter.RunsOf("app"));
        var simulator = Assert.Single(_starter.RunsOf("simulator"));
        var appRunning = true;
        _database.WhileApplying = _ =>
        {
            appRunning = !first.HasExited || _starter.RunsOf("app").Count > 1;
            return Task.CompletedTask;
        };

        var result = await _copies.ApplyTemplateAsync(copy.VisitorId);

        Assert.Equal(TemplateResult.Applied, result);
        Assert.False(appRunning);
        Assert.Equal([copy.DataFolder], _database.Templates);
        Assert.True(first.Stopped);
        Assert.Equal(2, _starter.RunsOf("app").Count);
        await copy.App.Listening;

        // The visitor's simulated board, its relays and the boards they added, go on as they are.
        Assert.False(simulator.HasExited);
        Assert.Single(_starter.RunsOf("simulator"));
        Assert.False(_copies.Visit(copy.VisitorId).OffersTemplate);
    }

    [Fact]
    public async Task Should_ApplyTheTemplate_OnlyOnce_AndNotAfterTheSetup()
    {
        var applied = Start(_copies);
        await applied.Started;
        Assert.Equal(TemplateResult.Applied, await _copies.ApplyTemplateAsync(applied.VisitorId));

        // Twice, e.g. from two tabs: the second finds it chosen.
        Assert.Equal(TemplateResult.Refused, await _copies.ApplyTemplateAsync(applied.VisitorId));

        var declined = Start(_copies);
        await declined.Started;
        Assert.True(_copies.Visit(declined.VisitorId).OffersTemplate);
        Assert.True(_copies.DeclineTemplate(declined.VisitorId));
        Assert.False(_copies.Visit(declined.VisitorId).OffersTemplate);
        Assert.Equal(TemplateResult.Refused, await _copies.ApplyTemplateAsync(declined.VisitorId));

        Assert.Equal([applied.DataFolder], _database.Templates);
        Assert.Single(_starter.RunsOf("app"), r => r.Start.Environment["ConnectionStrings__Default"].Contains(declined.DataFolder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_StartTheAppAgainAsItWas_WhenTheVisitorGotFurtherInTheSetup()
    {
        _database.Applies = false;
        var copy = Start(_copies);
        await copy.Started;

        Assert.Equal(TemplateResult.Refused, await _copies.ApplyTemplateAsync(copy.VisitorId));

        Assert.Equal(2, _starter.RunsOf("app").Count);
        await copy.App.Listening;
        Assert.False(_copies.Visit(copy.VisitorId).OffersTemplate);
    }

    [Fact]
    public async Task Should_ApplyNoTemplate_WithoutTheVisitorsOwnCopy()
    {
        var other = Start(_copies);
        await other.Started;
        var kept = _copies.Visit(null).NewId!;

        Assert.Equal(TemplateResult.None, await _copies.ApplyTemplateAsync(kept));
        Assert.Equal(TemplateResult.None, await _copies.ApplyTemplateAsync("0123456789abcdef0123456789abcdef"));
        Assert.False(_copies.DeclineTemplate(kept));

        await _copies.EndAsync(other.VisitorId);
        Assert.Equal(TemplateResult.None, await _copies.ApplyTemplateAsync(other.VisitorId));

        Assert.Empty(_database.Templates);
        Assert.Single(_starter.RunsOf("app"));
    }

    [Fact]
    public async Task Should_StartNothingAgain_WhenTheCopyEndsWhileTheTemplateIsApplied()
    {
        var copy = Start(_copies);
        await copy.Started;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.WhileApplying = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var applying = _copies.ApplyTemplateAsync(copy.VisitorId);
        await entered.Task;
        await _copies.EndAsync(copy.VisitorId);

        Assert.Equal(TemplateResult.None, await applying);
        Assert.Single(_starter.RunsOf("app"));
        Assert.All(_starter.Runs, r => Assert.True(r.HasExited));
        Assert.Empty(_folder.CopyFolders());
        Assert.Empty(_database.Templates);
    }

    [Fact]
    public async Task Should_ApplyNoTemplate_ToACopyThatDidntStart()
    {
        var database = new GatedCopyDatabase { Fails = true };
        await using var copies = Copies(maxVisitors: 3, database);
        var copy = Start(copies);
        await database.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var applying = copies.ApplyTemplateAsync(copy.VisitorId);
        database.Release.TrySetResult();

        Assert.Equal(TemplateResult.None, await applying);
        Assert.True(copy.Stopped.IsCompleted);
        Assert.Empty(_starter.Runs);
    }

    // A visitor as a browser is one: the page, then its frames.
    private static VisitorCopy Start(VisitorCopies copies)
    {
        var visit = copies.Visit(null);
        Assert.Equal(VisitKind.Kept, visit.Kind);
        var use = copies.Use(visit.NewId!);
        Assert.Equal(UseKind.Copy, use.Kind);
        return use.Copy!;
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

        /// <summary>Whether making the database fails once released.</summary>
        public bool Fails { get; init; }

        public async Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            if (Fails)
            {
                throw new IOException("disk");
            }

            File.WriteAllText(Path.Combine(dataFolder, "contactor.db"), "");
        }

        public Task<bool> ApplyTemplateAsync(string dataFolder, TimeZoneInfo timeZone, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }
    }
}
