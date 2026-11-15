using System.Collections.Concurrent;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// A command fails (here a timeout) after an earlier one of the same schedule tick or auto-off check went out: the one
/// that went out keeps its run, its auto-off and its event, and the failed one is tried again at the next tick or check.
/// </summary>
public sealed class FailAfterSendTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    // 14:05 local on the test's day; it starts at 14:02.
    private static readonly DateTimeOffset OnTime = new(2026, 11, 4, 13, 5, 0, TimeSpan.Zero);

    private readonly ErrorCollector _errors = new();

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
            services.AddSchedules(configuration);
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
        Assert.Contains("Scheduler could not switch relay 5 on relay board 1; the commands that went out before it are saved", _errors.Lines);

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
        Assert.Contains("Scheduler could not switch relay 5 on relay board 1; the commands that went out before it are saved", _errors.Lines);

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
        Assert.Contains("Auto-off could not turn off relay 5 on relay board 1; the OFFs that went out before it are saved", _errors.Lines);

        // The next check turns relay 5 off.
        _board.BeforeOff = null;
        Assert.Equal([5], await AutoOff.CheckAsync(Cancel));
        Assert.False(_board.State.IsOn(5));
        Assert.Null((await RunAsync(5)).OnSinceUtc);
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
