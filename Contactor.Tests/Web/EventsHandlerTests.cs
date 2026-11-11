using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class EventsHandlerTests : IAsyncLifetime
{
    // 15:00 local (UTC+1 in November).
    private static readonly DateTime Now = new(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc);

    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private EventsHandler Handler => _database.Services.GetRequiredService<EventsHandler>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddEvents(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_ListNewestFirst_WithNamesFromTheDatabase()
    {
        await AddAsync(
            Event("jane@example.com", EventActions.On, Now.AddHours(-2), relayId: 1),
            Event(EventActors.Schedule, EventActions.Off, Now.AddHours(-1), relayId: 1));
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Users.Add(new User { Email = "jane@example.com", DisplayName = "Jane" });
            await db.SaveChangesAsync(Cancel);
        }

        var rows = await Handler.GetAsync(EventFilter.All, beforeId: null, 50, Cancel);

        Assert.Equal(["Schedule", "Jane"], rows.Select(r => r.View.Who));
        Assert.Equal("Relay 1", rows[0].View.Target);
        Assert.Equal(new DateTime(2026, 11, 4, 14, 0, 0), rows[0].TimestampLocal);
    }

    [Theory]
    [InlineData(EventFilter.People, new[] { "on" })]
    [InlineData(EventFilter.Automatic, new[] { "connection-lost", "off" })]
    [InlineData(EventFilter.All, new[] { "connection-lost", "off", "on" })]
    public async Task Should_FilterPeopleFromAutomatic(EventFilter filter, string[] actions)
    {
        await AddAsync(
            Event("jane@example.com", EventActions.On, Now.AddHours(-3), relayId: 1),
            Event(EventActors.AutoOff, EventActions.Off, Now.AddHours(-2), relayId: 1),
            Event(EventActors.System, EventActions.ConnectionLost, Now.AddHours(-1), relayId: null));

        var rows = await Handler.GetAsync(filter, beforeId: null, 50, Cancel);

        Assert.Equal(actions, await ActionsInOrderAsync(rows));
    }

    [Fact]
    public async Task Should_PageByIdWithoutRepeatingRows()
    {
        await AddAsync([.. Enumerable.Range(0, 120).Select(i => Event("jane@example.com", EventActions.On, Now.AddMinutes(-i), relayId: 1))]);

        var first = await Handler.GetAsync(EventFilter.All, beforeId: null, EventsHandler.PageSize, Cancel);
        await AddAsync(Event("john@example.com", EventActions.Off, Now, relayId: 1));
        var second = await Handler.GetAsync(EventFilter.All, first[^1].Id, EventsHandler.PageSize, Cancel);
        var third = await Handler.GetAsync(EventFilter.All, second[^1].Id, EventsHandler.PageSize, Cancel);

        Assert.Equal((50, 50, 20), (first.Count, second.Count, third.Count));
        Assert.Equal(120, first.Concat(second).Concat(third).Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain(second, r => r.View.Who == "john@example.com");
    }

    [Fact]
    public async Task Should_OrderByTime_WhenAnEventIsLoggedLate()
    {
        await AddAsync(
            Event("jane@example.com", EventActions.On, Now.AddMinutes(-10), relayId: 1),
            Event(EventActors.System, EventActions.ConnectionLost, Now.AddMinutes(-20), relayId: null));

        var rows = await Handler.GetAsync(EventFilter.All, beforeId: null, 50, Cancel);

        Assert.Equal(["on", "connection-lost"], await ActionsInOrderAsync(rows));
    }

    [Fact]
    public async Task Should_PageAcrossEventsWithTheSameTime()
    {
        await AddAsync([.. Enumerable.Range(0, 5).Select(_ => Event("jane@example.com", EventActions.On, Now, relayId: 1))]);

        var first = await Handler.GetAsync(EventFilter.All, beforeId: null, 2, Cancel);
        var second = await Handler.GetAsync(EventFilter.All, first[^1].Id, 2, Cancel);
        var third = await Handler.GetAsync(EventFilter.All, second[^1].Id, 2, Cancel);

        Assert.Equal(5, first.Concat(second).Concat(third).Select(r => r.Id).Distinct().Count());
        Assert.Single(third);
    }

    [Fact]
    public async Task Should_ReturnNothing_WhenPagingFromAnUnknownEvent()
    {
        await AddAsync(Event("jane@example.com", EventActions.On, Now, relayId: 1));

        Assert.Empty(await Handler.GetAsync(EventFilter.All, beforeId: 999, 50, Cancel));
    }

    [Fact]
    public async Task Should_GroupByLocalDay()
    {
        // Logged in time order, as the app logs them.
        await AddAsync(
            Event(EventActors.Schedule, EventActions.On, new DateTime(2026, 11, 2, 22, 30, 0, DateTimeKind.Utc), relayId: 1), // 23:30 on the 2nd, local.
            Event(EventActors.Schedule, EventActions.On, new DateTime(2026, 11, 2, 23, 30, 0, DateTimeKind.Utc), relayId: 1), // 00:30 on the 3rd.
            Event(EventActors.Schedule, EventActions.On, Now, relayId: 1));

        var days = Handler.GroupByDay(await Handler.GetAsync(EventFilter.All, beforeId: null, 50, Cancel));

        Assert.Equal(["TODAY", "YESTERDAY", "MONDAY 2 NOV"], days.Select(d => d.Heading));
        Assert.All(days, d => Assert.Single(d.Rows));
    }

    [Theory]
    [InlineData(2026, 10, 25, "SUNDAY 25 OCT")]
    [InlineData(2025, 12, 31, "WEDNESDAY 31 DEC 2025")]
    public void Should_NameOlderDays(int year, int month, int day, string heading)
    {
        Assert.Equal(heading, EventsHandler.Heading(new DateTime(year, month, day), new DateTime(2026, 11, 4)));
    }

    [Fact]
    public async Task Should_ListOnlyTheGroupsEvents_WithItsName()
    {
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            db.RelayGroups.Add(new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan });
            await db.SaveChangesAsync(Cancel);
        }

        var groupOn = Event("jane@example.com", EventActions.GroupOn, Now.AddHours(-2), relayId: null);
        groupOn.GroupId = 1;
        var otherGroup = Event(EventActors.System, EventActions.GroupOff, Now.AddHours(-1), relayId: null);
        otherGroup.GroupId = 2;
        await AddAsync(groupOn, otherGroup, Event("jane@example.com", EventActions.On, Now, relayId: 1));

        var rows = await Handler.GetAsync(EventFilter.All, beforeId: null, 50, Cancel, groupId: 1);

        var row = Assert.Single(rows);
        Assert.Equal(("turned on the group", "Fan heater"), (row.View.What, row.View.Target));
    }

    private static EventLogEntry Event(string actor, string action, DateTime timestampUtc, int? relayId)
    {
        return new EventLogEntry { Actor = actor, Action = action, TimestampUtc = timestampUtc, RelayId = relayId };
    }

    private async Task AddAsync(params EventLogEntry[] entries)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        db.EventLog.AddRange(entries);
        await db.SaveChangesAsync(Cancel);
    }

    private async Task<string[]> ActionsInOrderAsync(IReadOnlyList<EventRow> rows)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var actions = await db.EventLog.ToDictionaryAsync(e => e.Id, e => e.Action, Cancel);
        return [.. rows.Select(r => actions[r.Id])];
    }
}
