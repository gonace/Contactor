using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Groups;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class SchedulesHandlerTests : IAsyncLifetime
{
    private const string John = "john@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeOnly SixThirty = new(6, 30);
    private static readonly TimeOnly Eight = new(8, 0);

    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private SchedulesHandler Handler => _database.Services.GetRequiredService<SchedulesHandler>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddSchedules(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_AddAnActiveScheduleAndLogIt()
    {
        Assert.Null(await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, SixThirty, Eight, false), John, Cancel));

        var schedule = Assert.Single(await Handler.ListAsync(1, Cancel));
        Assert.Equal((ScheduleDays.Weekdays, SixThirty, Eight, true), (schedule.Days, schedule.OnTime, schedule.OffTime, schedule.IsEnabled));
        var entry = Assert.Single(await EventsAsync());
        Assert.Equal((EventActions.ScheduleAdd, John, 1), (entry.Action, entry.Actor, entry.RelayId!.Value));
        Assert.Equal("added schedule Mon–Fri 06:30–08:00", EventText.For(entry, "Relay 1", "John").What);
    }

    [Fact]
    public async Task Should_KeepTimesToTheMinute()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.All, new TimeOnly(6, 30, 45), new TimeOnly(8, 0, 10), true), John, Cancel);

        var schedule = Assert.Single(await Handler.ListAsync(1, Cancel));
        Assert.Equal((SixThirty, Eight), (schedule.OnTime, schedule.OffTime));
    }

    [Fact]
    public async Task Should_ChangeAScheduleAndLogTheNewTimes()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, SixThirty, Eight, true), John, Cancel);
        var id = (await Handler.ListAsync(1, Cancel))[0].Id;

        await Handler.SaveAsync(new ScheduleModel(id, 1, ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0), true), "jane@example.com", Cancel);

        var schedule = await Handler.GetAsync(id, Cancel);
        Assert.Equal((ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0)), (schedule!.Days, schedule.OnTime, schedule.OffTime));
        var edit = (await EventsAsync())[1];
        Assert.Equal("changed schedule to Saturday 22:00–01:00", EventText.For(edit, "Relay 1", "Jane").What);
    }

    [Fact]
    public async Task Should_LogNothing_WhenSavedUnchanged()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, SixThirty, Eight, true), John, Cancel);
        var id = (await Handler.ListAsync(1, Cancel))[0].Id;

        Assert.Null(await Handler.SaveAsync(new ScheduleModel(id, 1, ScheduleDays.Weekdays, SixThirty, Eight, true), John, Cancel));

        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task Should_PauseAndResumeAndLogBoth()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, SixThirty, Eight, true), John, Cancel);
        var id = (await Handler.ListAsync(1, Cancel))[0].Id;

        Assert.True(await Handler.SetEnabledAsync(id, false, John, Cancel));
        Assert.True(await Handler.SetEnabledAsync(id, false, John, Cancel));
        Assert.True(await Handler.SetEnabledAsync(id, true, John, Cancel));

        Assert.True((await Handler.GetAsync(id, Cancel))!.IsEnabled);
        var texts = (await EventsAsync()).Skip(1).Select(e => EventText.For(e, "Relay 1", "John").What);
        Assert.Equal(["paused schedule Mon–Fri 06:30–08:00", "resumed schedule Mon–Fri 06:30–08:00"], texts);
    }

    [Fact]
    public async Task Should_DeleteAndStillSayWhichScheduleItWas()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 2, ScheduleDays.All, SixThirty, Eight, true), John, Cancel);
        var id = (await Handler.ListAsync(2, Cancel))[0].Id;

        await Handler.DeleteAsync(id, John, Cancel);

        Assert.Empty(await Handler.ListAsync(2, Cancel));
        Assert.Null(await Handler.GetAsync(id, Cancel));
        Assert.Equal("deleted schedule Every day 06:30–08:00", EventText.For((await EventsAsync())[1], "Relay 2", "John").What);
        Assert.False(await Handler.SetEnabledAsync(id, false, John, Cancel));
    }

    [Theory]
    [InlineData(ScheduleDays.None, 6, 30, 8, 0, "Choose at least one day.")]
    [InlineData((ScheduleDays)128, 6, 30, 8, 0, "Choose at least one day.")]
    [InlineData(ScheduleDays.Monday, 7, 0, 7, 0, "Turning on and turning off can't be at the same time.")]
    public async Task Should_RefuseSchedulesThatCannotRun(ScheduleDays days, int onHour, int onMinute, int offHour, int offMinute, string message)
    {
        var model = new ScheduleModel(0, 1, days, new TimeOnly(onHour, onMinute), new TimeOnly(offHour, offMinute), true);

        Assert.Equal(message, await Handler.SaveAsync(model, John, Cancel));
        Assert.Empty(await Handler.ListAsync(1, Cancel));
    }

    [Fact]
    public async Task Should_RefuseAScheduleForARelayThatDoesNotExist()
    {
        Assert.Equal("The relay doesn't exist.", await Handler.SaveAsync(new ScheduleModel(0, 9, ScheduleDays.All, SixThirty, Eight, true), John, Cancel));
    }

    [Fact]
    public async Task Should_SaySoWhenEditingADeletedSchedule()
    {
        Assert.Equal("The schedule no longer exists.", await Handler.SaveAsync(new ScheduleModel(42, 1, ScheduleDays.All, SixThirty, Eight, true), John, Cancel));
    }

    [Fact]
    public async Task Should_ListLikeACalendarWeek()
    {
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Saturday, new TimeOnly(8, 0), new TimeOnly(12, 0), true), John, Cancel);
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, new TimeOnly(16, 0), new TimeOnly(18, 0), true), John, Cancel);
        await Handler.SaveAsync(new ScheduleModel(0, 1, ScheduleDays.Weekdays, SixThirty, Eight, true), John, Cancel);

        var list = await Handler.ListAsync(1, Cancel);

        Assert.Equal(["Mon–Fri 06:30", "Mon–Fri 16:00", "Saturday 08:00"], list.Select(s => $"{ScheduleText.ShortDays(s.Days)} {ScheduleText.Time(s.OnTime)}"));
    }

    [Fact]
    public void Should_ShowThePlainWords_WhenTheDetailsAreMissing()
    {
        var entry = new EventLogEntry { Actor = John, Action = EventActions.ScheduleAdd, RelayId = 1, Details = """{"days":999}""" };

        Assert.Equal("added a schedule", EventText.For(entry, "Relay 1", "John").What);
    }

    private async Task<List<EventLogEntry>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.OrderBy(e => e.Id).ToListAsync(Cancel);
    }
}
