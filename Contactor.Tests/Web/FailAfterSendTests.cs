using System.Collections.Concurrent;
using System.Data.Common;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// A command (here a timeout) or a database call fails after an earlier command of the same schedule tick or auto-off
/// check went out: the one that went out keeps its run, its auto-off and its event, and the failed one is tried again
/// at the next tick or check.
/// </summary>
public sealed class FailAfterSendTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    // 14:05 local on the test's day; it starts at 14:02.
    private static readonly DateTimeOffset OnTime = new(2026, 11, 4, 13, 5, 0, TimeSpan.Zero);

    private readonly ErrorCollector _errors = new();
    private readonly FailingQueries _queries = new();

    private readonly FakeRelayBoard _board = new(8);
    private readonly FixedTimeProvider _time = new(Now);
    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    private AutoOffService AutoOff => _database.Services.GetRequiredService<AutoOffService>();

    private SchedulerService Scheduler => _database.Services.GetRequiredService<SchedulerService>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddBoards(configuration);
            services.AddSingleton<TimeProvider>(_time);
            services.AddLogging(logging => logging.AddProvider(_errors));
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddOwnRules(configuration);
            services.AddSchedules(configuration);
            services.ConfigureDbContext<ContactorDbContext>(options => options.AddInterceptors(_queries));
        });
        await _database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await _database.Services.UseFakeBoardAsync(_board);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_RecordTheScheduleOnThatWentOut_WhenTheNextOnTimesOut()
    {
        await AddScheduleAsync(relayId: 2);
        await AddScheduleAsync(relayId: 5);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;
        _board.BeforeOn = TimeOutTheSecond();

        await Assert.ThrowsAsync<TimeoutException>(() => Scheduler.TickAsync(Cancel));

        Assert.Equal((true, false), (_board.State.IsOn(2), _board.State.IsOn(5)));
        var run = await RunAsync(2);
        Assert.Equal((EventActors.Schedule, (DateTime?)OnTime.UtcDateTime, (DateTime?)null), (run.TurnedOnBy, run.OnSinceUtc, run.AutoOffAtUtc));
        Assert.Equal([(2, EventActors.Schedule, EventActions.On)], await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(5, on: true));
        Assert.Contains("Scheduler failed at relay 5 on relay board 1; the commands that went out before it are saved", _errors.Lines);

        // The next tick turns relay 5 on, and leaves relay 2 alone.
        _board.BeforeOn = null;
        var sent = _board.Sent.Count;
        _time.Now = OnTime.AddMinutes(1);
        await Scheduler.TickAsync(Cancel);
        Assert.True(_board.State.IsOn(5));
        Assert.Equal(sent + 1, _board.Sent.Count);
        Assert.Equal([(2, EventActors.Schedule, EventActions.On), (5, EventActors.Schedule, EventActions.On)], await EventsAsync());
    }

    // Relay 5's schedule ends at 14:05, when relay 2's starts.
    [Fact]
    public async Task Should_RecordTheScheduleOnThatWentOut_WhenTheNextOffTimesOut()
    {
        await AddScheduleAsync(relayId: 2);
        await AddScheduleAsync(relayId: 5, on: new TimeOnly(14, 0), off: new TimeOnly(14, 5));
        await Scheduler.TickAsync(Cancel);
        Assert.True(_board.State.IsOn(5));
        _time.Now = OnTime;
        _board.BeforeOff = TimeOutTheFirst();

        await Assert.ThrowsAsync<TimeoutException>(() => Scheduler.TickAsync(Cancel));

        Assert.Equal((true, true), (_board.State.IsOn(2), _board.State.IsOn(5)));
        Assert.Equal(EventActors.Schedule, (await RunAsync(2)).TurnedOnBy);
        Assert.Equal(EventActors.Schedule, (await RunAsync(5)).TurnedOnBy);
        Assert.Equal([(5, EventActors.Schedule, EventActions.On), (2, EventActors.Schedule, EventActions.On)], await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(5, on: false));
        Assert.Contains("Scheduler failed at relay 5 on relay board 1; the commands that went out before it are saved", _errors.Lines);

        // The next tick turns relay 5 off, and leaves relay 2 alone.
        _board.BeforeOff = null;
        var sent = _board.Sent.Count;
        _time.Now = OnTime.AddMinutes(1);
        await Scheduler.TickAsync(Cancel);
        Assert.Equal((true, false), (_board.State.IsOn(2), _board.State.IsOn(5)));
        Assert.Equal(sent + 1, _board.Sent.Count);
        Assert.Null((await RunAsync(5)).OnSinceUtc);
        Assert.Equal(
            [(5, EventActors.Schedule, EventActions.On), (2, EventActors.Schedule, EventActions.On), (5, EventActors.Schedule, EventActions.Off)],
            await EventsAsync());
    }

    [Fact]
    public async Task Should_RecordTheAutoOffThatWentOut_WhenTheNextOffTimesOut()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await Control.TurnOnAsync(5, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        _board.BeforeOff = TimeOutTheSecond();

        await Assert.ThrowsAsync<TimeoutException>(() => AutoOff.CheckAsync(_database.Services.Live(), Cancel));

        Assert.Equal((false, true), (_board.State.IsOn(2), _board.State.IsOn(5)));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal(Jane, (await RunAsync(5)).TurnedOnBy);
        Assert.Equal([(2, Jane, EventActions.On), (5, Jane, EventActions.On), (2, EventActors.AutoOff, EventActions.Off)], await EventsAsync());
        Assert.False(_database.Services.Live().Expected.IsPending(5, on: false));
        Assert.Contains("Auto-off failed at relay 5 on relay board 1; the OFFs that went out before it are saved", _errors.Lines);

        // The next check turns relay 5 off.
        _board.BeforeOff = null;
        Assert.Equal([5], await AutoOff.CheckAsync(Cancel));
        Assert.False(_board.State.IsOn(5));
        Assert.Null((await RunAsync(5)).OnSinceUtc);
    }

    [Fact]
    public async Task Should_RecordTheScheduleOnThatWentOut_WhenTheDatabaseFailsAfterTheNextOn()
    {
        await AddScheduleAsync(relayId: 2);
        await AddScheduleAsync(relayId: 5);
        await Scheduler.TickAsync(Cancel);
        _time.Now = OnTime;

        // Relay 5's ON goes out; reading its run fails.
        var commands = 0;
        _board.AfterSwitch = () =>
        {
            if (++commands == 2)
            {
                _queries.FailRunRead(skip: 0);
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<SqliteException>(() => Scheduler.TickAsync(Cancel));

        Assert.Equal((true, true), (_board.State.IsOn(2), _board.State.IsOn(5)));
        var run = await RunAsync(2);
        Assert.Equal((EventActors.Schedule, (DateTime?)OnTime.UtcDateTime), (run.TurnedOnBy, run.OnSinceUtc));
        Assert.Equal([(2, EventActors.Schedule, EventActions.On), (5, EventActors.Schedule, EventActions.On)], await EventsAsync());
        Assert.Contains("Scheduler failed at relay 5 on relay board 1; the commands that went out before it are saved", _errors.Lines);

        // The next tick sends nothing: both relays are on.
        _board.AfterSwitch = null;
        var sent = _board.Sent.Count;
        _time.Now = OnTime.AddMinutes(1);
        await Scheduler.TickAsync(Cancel);
        Assert.Equal(sent, _board.Sent.Count);
        Assert.Equal(EventActors.Schedule, (await RunAsync(2)).TurnedOnBy);
    }

    [Fact]
    public async Task Should_RecordTheAutoOffThatWentOut_WhenTheDatabaseFailsAfterIt()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        await Control.TurnOnAsync(5, Jane, Cancel);

        // Relay 5 went off outside the app: its run is ended without a command.
        _board.SetRelay(5, on: false);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        // Relay 2's OFF goes out and its run ends; reading relay 5's run fails.
        _board.AfterOff = () =>
        {
            _queries.FailRunRead(skip: 1);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<SqliteException>(() => AutoOff.CheckAsync(_database.Services.Live(), Cancel));

        Assert.Equal((false, false), (_board.State.IsOn(2), _board.State.IsOn(5)));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal(Jane, (await RunAsync(5)).TurnedOnBy);
        Assert.Equal([(2, Jane, EventActions.On), (5, Jane, EventActions.On), (2, EventActors.AutoOff, EventActions.Off)], await EventsAsync());
        Assert.Contains("Auto-off failed at relay 5 on relay board 1; the OFFs that went out before it are saved", _errors.Lines);

        // The next check ends relay 5's run, and sends nothing.
        _board.AfterOff = null;
        var sent = _board.Sent.Count;
        Assert.Empty(await AutoOff.CheckAsync(Cancel));
        Assert.Equal(sent, _board.Sent.Count);
        Assert.Null((await RunAsync(5)).OnSinceUtc);
    }

    [Fact]
    public async Task Should_RecordTheAutoOffThatWentOut_WhenTheDatabaseFailsAfterTheLoop()
    {
        await Control.TurnOnAsync(2, Jane, Cancel);
        _time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        // Relay 2's OFF goes out and its run ends; reading the runs of the relays that are off fails.
        _board.AfterOff = () =>
        {
            _queries.FailRunRead(skip: 1);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<SqliteException>(() => AutoOff.CheckAsync(_database.Services.Live(), Cancel));

        Assert.False(_board.State.IsOn(2));
        Assert.Null((await RunAsync(2)).OnSinceUtc);
        Assert.Equal([(2, Jane, EventActions.On), (2, EventActors.AutoOff, EventActions.Off)], await EventsAsync());
        Assert.Contains("Auto-off could not end the runs of the relays that are off on relay board 1; the OFFs that went out are saved", _errors.Lines);
    }

    // A schedule's ON refused while the board was being removed is tried again at the next tick, also after that try
    // fails: the window began in an earlier tick, so nothing else would turn the relay on.
    [Fact]
    public async Task Should_TryTheScheduleOnAgain_WhenItsRetryFails()
    {
        await AddScheduleAsync(relayId: 2);
        await Scheduler.TickAsync(Cancel);
        var live = _database.Services.Live();
        await live.CloseAsync();
        _time.Now = OnTime;
        await Scheduler.TickAsync(Cancel);
        Assert.False(_board.State.IsOn(2));

        live.Reopen();
        _board.BeforeOn = TimeOutTheFirst();
        _time.Now = OnTime.AddMinutes(1);
        await Assert.ThrowsAsync<TimeoutException>(() => Scheduler.TickAsync(Cancel));
        Assert.False(_board.State.IsOn(2));

        _board.BeforeOn = null;
        _time.Now = OnTime.AddMinutes(2);
        await Scheduler.TickAsync(Cancel);
        Assert.True(_board.State.IsOn(2));
        Assert.Equal(EventActors.Schedule, (await RunAsync(2)).TurnedOnBy);
        Assert.Equal([(2, EventActors.Schedule, EventActions.On)], await EventsAsync());
    }

    // The first command goes through; the second times out before it reaches the board.
    private static Func<Task> TimeOutTheSecond()
    {
        var commands = 0;
        return () => ++commands == 2 ? throw new TimeoutException("The board didn't answer.") : Task.CompletedTask;
    }

    // The first OFF times out before it reaches the board.
    private static Func<Task> TimeOutTheFirst()
    {
        var commands = 0;
        return () => ++commands == 1 ? throw new TimeoutException("The board didn't answer.") : Task.CompletedTask;
    }

    private async Task<RelayRuntime> RunAsync(int relayId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    // The app's own events, as (relay, actor, action).
    private async Task<List<(int RelayId, string Actor, string Action)>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return [.. (await db.EventLog.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Cancel)).Select(e => (e.RelayId ?? 0, e.Actor, e.Action))];
    }

    // 14:05 to 14:10 unless told otherwise.
    private async Task AddScheduleAsync(int relayId, TimeOnly? on = null, TimeOnly? off = null)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            Days = ScheduleDays.All,
            OnTime = on ?? new TimeOnly(14, 5),
            OffTime = off ?? new TimeOnly(14, 10),
            IsEnabled = true,
            CreatedBy = Jane,
        });
        await db.SaveChangesAsync(Cancel);
    }

    // Fails a chosen read of a relay's run, as a database error would.
    private sealed class FailingQueries : DbCommandInterceptor
    {
        private int _skip = -1;

        // Lets <paramref name="skip"/> reads of a relay's run through, then fails the next one.
        public void FailRunRead(int skip)
        {
            Volatile.Write(ref _skip, skip);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"RelayRuntime\"", StringComparison.Ordinal)
                && Volatile.Read(ref _skip) >= 0
                && Interlocked.Decrement(ref _skip) == -1)
            {
                throw new SqliteException("database is locked", 5);
            }

            return ValueTask.FromResult(result);
        }
    }

    // The error lines logged, by their message.
    private sealed class ErrorCollector : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Error;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _lines.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
