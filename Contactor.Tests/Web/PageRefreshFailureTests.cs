using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// A page's refresh, started from an event and not awaited, whose database read fails: it is logged once as a warning,
/// and the page keeps what it showed. A page closing while it refreshes logs nothing.
/// </summary>
public sealed class PageRefreshFailureTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(Relays), "the relays, groups and inputs")]
    [InlineData(typeof(RelayDetail), "the relay")]
    [InlineData(typeof(GroupDetail), "the group")]
    [InlineData(typeof(InputDetail), "the input")]
    [InlineData(typeof(AnalogInputDetail), "the analog input")]
    [InlineData(typeof(Events), "the events")]
    [InlineData(typeof(Debug), "the board filter's counts")]
    public async Task Should_LogAFailedRefreshOnce_AndKeepThePage(Type pageType, string what)
    {
        var queries = new FailingQueries();
        var log = new WarningCollector();
        await using var app = await StartAppAsync(queries, log, pageType == typeof(AnalogInputDetail));
        using var page = await StartAsync(app, pageType);
        var changes = app.Services.GetRequiredService<RelayChanges>();
        var before = await page.TextAsync();

        queries.Fail(_ => true, () => new SqliteException("disk I/O error", 10));
        await page.Dispatcher.InvokeAsync(changes.Notify);
        await DrainAsync(page, queries, log, () => log.Of(pageType).Count > 0);
        queries.Stop();

        Assert.NotEqual(0, queries.Failures);
        var warning = Assert.Single(log.Of(pageType));
        Assert.IsType<SqliteException>(warning.Exception);
        Assert.Equal(what, warning.What);
        Assert.Empty(page.Errors);
        Assert.Equal(before, await page.TextAsync());

        // The next refresh tries again, and works.
        var renders = page.PageRenders;
        await page.Dispatcher.InvokeAsync(changes.Notify);
        await DrainAsync(page, queries, log, () => page.PageRenders > renders);
        Assert.Single(log.Of(pageType));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_LogAFailedReloadOfTheDebuggingSetting_InTheTabBar()
    {
        var queries = new FailingQueries();
        var log = new WarningCollector();
        await using var app = await StartAppAsync(queries, log, analog: false);
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            db.Users.Add(new User { Email = John, DisplayName = "John" });
            await db.SaveChangesAsync(Cancel);
        }

        using var page = await PageRenderer.StartAsync<TabBar>(app.Services, John, "John");
        var setting = app.Services.GetRequiredService<BoardTrafficSetting>();

        // Only the tab bar's read of the setting, not the save that announces it.
        queries.Fail(sql => sql.StartsWith("SELECT \"u\".\"ShowBoardTraffic\"", StringComparison.Ordinal), () => new SqliteException("disk I/O error", 10));
        await page.Dispatcher.InvokeAsync(() => setting.SetAsync(John, on: true, Cancel));
        await DrainAsync(page, queries, log, () => log.Of(typeof(TabBar)).Count > 0);
        queries.Stop();

        Assert.NotEqual(0, queries.Failures);
        var warning = Assert.Single(log.Of(typeof(TabBar)));
        Assert.IsType<SqliteException>(warning.Exception);
        Assert.Equal("the person's Debugging setting", warning.What);
        Assert.Empty(page.Errors);
    }

    // As when the page or its connection closes during the read: nothing to log.
    [Theory]
    [InlineData(typeof(Relays), false)]
    [InlineData(typeof(Relays), true)]
    [InlineData(typeof(RelayDetail), false)]
    [InlineData(typeof(RelayDetail), true)]
    public async Task Should_LogNothing_WhenTheRefreshEndsBecauseThePageClosed(Type pageType, bool cancelled)
    {
        var queries = new FailingQueries();
        var log = new WarningCollector();
        await using var app = await StartAppAsync(queries, log, analog: false);
        using var page = await StartAsync(app, pageType);
        var changes = app.Services.GetRequiredService<RelayChanges>();

        queries.Fail(_ => true, () => cancelled ? new OperationCanceledException() : new ObjectDisposedException("ContactorDbContext"));
        await page.Dispatcher.InvokeAsync(changes.Notify);
        await DrainAsync(page, queries, log, () => queries.Failures > 0);
        queries.Stop();

        Assert.NotEqual(0, queries.Failures);
        Assert.Empty(log.Of(pageType));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_LogNothing_WhenThePageClosedBeforeItsRefresh()
    {
        var queries = new FailingQueries();
        var log = new WarningCollector();
        await using var app = await StartAppAsync(queries, log, analog: false);
        var page = await StartAsync(app, typeof(Relays));
        var changes = app.Services.GetRequiredService<RelayChanges>();

        // The change comes from another thread, as from a board or another person's page, while the page's own thread is
        // busy: the refresh waits for it, and by then the page has closed. A closed page loads nothing.
        queries.Fail(_ => true, () => new SqliteException("disk I/O error", 10));
        await page.Dispatcher.InvokeAsync(() =>
        {
            var other = new Thread(changes.Notify);
            other.Start();
            other.Join();
            page.Close();
        });

        // The refresh was queued before this round, and a closed page ends it there.
        await DrainAsync(page, queries, log, () => true);
        queries.Stop();

        Assert.Equal(0, queries.Failures);
        Assert.Empty(log.Of(typeof(Relays)));
    }

    // The app, once what it does at start is done, so the test's refresh is the only one while reads fail:
    // - the board runs: its run reads the board's saved address first, on a thread of its own, and a failed read there
    //   stops the whole app, as any board's failed run does. The fake connection only runs after that read.
    // - with a board in contact (the analog one), the event recorder has told the pages about its first status, as it
    //   saves when contact began: a page open then would refresh too, and log a second warning. A board that has lost
    //   contact gives the recorder nothing to save at start.
    private static async Task<TestWebApp> StartAppAsync(FailingQueries queries, WarningCollector log, bool analog)
    {
        var board = analog ? new FakeRelayBoard(relayCount: 4, inputCount: 1, analog: new AnalogAbility(4, 1023, 5m)) : FakeRelayBoard.LostContact();
        var told = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services =>
            {
                TestWebApp.WithBoard(board)(services);
                queries.AddTo(services);
                services.AddSingleton<ILoggerProvider>(log);
                services.AddSingleton(_ =>
                {
                    var changes = new RelayChanges();
                    changes.Changed += (_, _) => told.TrySetResult();
                    return changes;
                });
            });
        try
        {
            var connection = Assert.IsType<FakeConnection>(app.Services.Live().Active?.Connection);
            for (var round = 0; !connection.Running || (analog && !told.Task.IsCompleted); round++)
            {
                Assert.True(round < 10_000, $"The app never finished starting: the board runs: {connection.Running}, the pages were told: {told.Task.IsCompleted}.");
                await Task.Delay(1, Cancel);
            }

            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<PageRenderer> StartAsync(TestWebApp app, Type pageType)
    {
        return pageType.Name switch
        {
            nameof(Relays) => await PageRenderer.StartAsync<Relays>(app.Services, John, "John"),
            nameof(RelayDetail) => await PageRenderer.StartAsync<RelayDetail>(app.Services, John, "John", Id(1)),
            nameof(GroupDetail) => await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", Id(await AddGroupAsync(app))),
            nameof(InputDetail) => await PageRenderer.StartAsync<InputDetail>(app.Services, John, "John", Id(1)),
            nameof(AnalogInputDetail) => await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(1)),
            nameof(Events) => await PageRenderer.StartAsync<Events>(app.Services, John, "John"),
            nameof(Debug) => await PageRenderer.StartAsync<Debug>(app.Services, John, "John"),
            _ => throw new ArgumentOutOfRangeException(nameof(pageType), pageType, null),
        };
    }

    private static Dictionary<string, object?> Id(int id)
    {
        return new() { ["Id"] = id };
    }

    // Straight into the database: setting up through GroupsHandler needs a board to see the relays are off.
    private static async Task<int> AddGroupAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.AddStep("Fan", 1, runOnSec: 300);
        group.AddStep("Heat", 2);
        await db.AddWithRuntimesAsync(group);
        return group.Id;
    }

    // Waits until the refresh reached the step done counts (a warning, a draw, a failed read), however its reads
    // complete. The count is read on the page's own thread, between its steps, so what the counted step queued there
    // (e.g. the catch after a failed read) is queued before the next round, which runs it. A refresh that never gets
    // there fails with what the app did: how many reads failed, the warnings with an exception, what went wrong in the
    // page, and what it shows.
    private static async Task DrainAsync(PageRenderer page, FailingQueries queries, WarningCollector log, Func<bool> done)
    {
        const int Rounds = 10_000;
        for (var round = 0; !await page.Dispatcher.InvokeAsync(done); round++)
        {
            if (round >= Rounds)
            {
                var warnings = string.Join("; ", log.All.Where(w => w.Exception is not null).Select(w => $"{w.Category}: {w.What} ({w.Exception!.GetType().Name}: {w.Exception.Message})"));
                var errors = string.Join("; ", page.Errors.Select(e => $"{e.GetType().Name}: {e.Message}"));
                Assert.Fail($"The refresh never reached the step the test waits for, after {round} rounds. Failed reads: {queries.Failures}. "
                    + $"Warnings: [{warnings}]. Errors in the page: [{errors}]. Page draws: {page.PageRenders}. The page: {await page.TextAsync()}");
            }

            await Task.Delay(1, Cancel);
        }

        await page.Dispatcher.InvokeAsync(() => { });
    }

    private sealed record Warning(string Category, Exception? Exception, string? What);

    /// <summary>Keeps every warning and worse, with the logger's category and the <c>What</c> field.</summary>
    private sealed class WarningCollector : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<Warning> _warnings = [];

        public IReadOnlyList<Warning> All
        {
            get
            {
                lock (_lock)
                {
                    return [.. _warnings];
                }
            }
        }

        public IReadOnlyList<Warning> Of(Type category)
        {
            lock (_lock)
            {
                return [.. _warnings.Where(w => w.Category == category.FullName)];
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this, categoryName);
        }

        public void Dispose()
        {
        }

        private void Add(Warning warning)
        {
            lock (_lock)
            {
                _warnings.Add(warning);
            }
        }

        private sealed class Logger : ILogger
        {
            private readonly WarningCollector _owner;
            private readonly string _category;

            public Logger(WarningCollector owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Warning;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var what = state is IReadOnlyList<KeyValuePair<string, object?>> fields
                    ? fields.FirstOrDefault(f => f.Key == "What").Value as string
                    : null;
                _owner.Add(new Warning(_category, exception, what));
            }
        }
    }
}
