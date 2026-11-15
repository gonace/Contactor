using Contactor.Demo;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Demo;

/// <summary>
/// The Workshop template: what it fills a new copy's database with, its three days of events, and that it leaves a
/// database the visitor took further as it is.
/// </summary>
public sealed class WorkshopTemplateTests : IDisposable
{
    // A Wednesday, at noon in Stockholm.
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 11, 11, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private readonly DemoFolder _folder = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    [Fact]
    public async Task Should_FillANewCopy_WithTheWorkshop_AsThePlanSays()
    {
        var dataFolder = await FilledAsync(Wednesday);

        await using var db = Open(dataFolder);
        var installation = await db.Installations.SingleAsync(Cancel);
        Assert.Equal(Wednesday.UtcDateTime, installation.SetupDoneUtc);
        var board = await db.Boards.SingleAsync(Cancel);
        Assert.Equal(("vm201", "Workshop"), (board.Type, board.Name));

        // No address or login of its own: the copy's settings point it at the visitor's simulated VM201.
        Assert.Equal(((string?)null, (int?)null, (string?)null), (board.Host, board.Port, board.Username));

        var relays = await db.Relays.OrderBy(r => r.Channel).ToListAsync(Cancel);
        Assert.Equal(
            [
                (1, "Fan", true, (int?)120), (2, "Heat", true, 120), (3, "Air compressor", true, 60), (4, "Lights", true, 240),
                (5, "Ventilation", true, 120), (6, "Outdoor light", true, 120), (7, "Relay 7", false, 120), (8, "Relay 8", false, 120),
            ],
            relays.Select(r => (r.Channel, r.Name, r.IsActive, r.MaxOnMinutes)).ToList());
        Assert.Equal(8, await db.RelayRuntimes.CountAsync(Cancel));
        Assert.All(await db.RelayRuntimes.ToListAsync(Cancel), r => Assert.Null(r.OnSinceUtc));

        var group = await db.RelayGroups.Include(g => g.Steps).ThenInclude(s => s.Members).SingleAsync(Cancel);
        Assert.Equal(("Fan heater", 120, board.Id), (group.Name, group.MaxOnMinutes, group.BoardId));
        var steps = group.Steps.OrderBy(s => s.Position).Select(s => (s.Position, Channel(relays, Assert.Single(s.Members).RelayId), s.Name, s.RunOnSec)).ToList();
        Assert.Equal([(1, 1, "Fan", 120), (2, 2, "Heat", 0)], steps);
        Assert.Equal(3, group.Steps.Single(s => s.Position == 2).StartDelaySec);
        var runtime = await db.GroupRuntimes.SingleAsync(Cancel);
        Assert.Equal((group.Id, GroupPhases.Idle, (string?)null), (runtime.GroupId, runtime.Phase, runtime.TurnedOnBy));

        var schedules = await db.Schedules.OrderBy(s => s.Id).ToListAsync(Cancel);
        Assert.Equal(
            [
                (5, ScheduleDays.Weekdays, new TimeOnly(7, 0), new TimeOnly(8, 0)),
                (6, ScheduleDays.All, new TimeOnly(16, 0), new TimeOnly(22, 0)),
            ],
            schedules.Select(s => (Channel(relays, s.RelayId!.Value), s.Days, s.OnTime, s.OffTime)).ToList());
        Assert.All(schedules, s => Assert.True(s.IsEnabled && s.GroupId is null));

        var door = await db.Inputs.SingleAsync(Cancel);
        Assert.Equal((1, "Door"), (door.Channel, door.Name));
        var action = await db.InputActions.SingleAsync(Cancel);
        Assert.Equal((door.Id, 4, (bool?)true, (bool?)null), (action.InputId, Channel(relays, action.RelayId!.Value), action.WhenOpened, action.WhenClosed));

        Assert.Equal(
            [("alex@example.com", "Alex"), ("sam@example.com", "Sam")],
            (await db.Users.OrderBy(u => u.Email).ToListAsync(Cancel)).Select(u => (u.Email, u.DisplayName)).ToList());

        // The scheduler carries on from now, with nothing to catch up.
        var progress = await db.BoardRuntimes.SingleAsync(Cancel);
        Assert.Equal(new DateTime(2026, 11, 11, 11, 0, 0, DateTimeKind.Utc), progress.LastTickUtc);
        Assert.Equal(Wednesday.UtcDateTime, progress.LastContactUtc);
    }

    [Fact]
    public async Task Should_TellOfThreeDays_ByAlexAndSam_EndingBeforeNow()
    {
        var dataFolder = await FilledAsync(Wednesday);

        await using var db = Open(dataFolder);
        var events = await db.EventLog.OrderBy(e => e.Id).ToListAsync(Cancel);
        var now = Wednesday.UtcDateTime;

        // In time order, so the door's last event is its latest.
        Assert.Equal(events.OrderBy(e => e.TimestampUtc).Select(e => e.Id), events.Select(e => e.Id));
        Assert.All(events, e => Assert.InRange(e.TimestampUtc, now - TimeSpan.FromDays(3), now - TimeSpan.FromMinutes(1)));
        Assert.True(events[0].TimestampUtc < now - TimeSpan.FromDays(2.5));
        Assert.Contains(events, e => e.Actor == WorkshopTemplate.Alex);
        Assert.Contains(events, e => e.Actor == WorkshopTemplate.Sam);
        var board = await db.Boards.SingleAsync(Cancel);
        Assert.All(events, e => Assert.Equal(board.Id, e.BoardId));

        // Heating mornings, the compressor now and then, the door opening and the schedules, as Events words them.
        var names = (await db.Users.ToListAsync(Cancel)).ToDictionary(u => u.Email, u => u.DisplayName);
        var relays = await db.Relays.ToDictionaryAsync(r => r.Id, r => r.Name, Cancel);
        var texts = events.Select(e => Text(e, names, relays)).ToList();
        Assert.Contains("Alex turned on the group · Fan heater · Fan → 3 s → Heat", texts);
        Assert.Contains("Run-on: Fan keeps running for 2 min · Fan heater", texts);
        Assert.Contains("Run-on done: Fan off · Fan heater", texts);
        Assert.Contains("Auto-off turned off the group after 2 h · Fan heater", texts);
        Assert.Contains(texts, t => t is "Sam turned on · Air compressor" or "Alex turned on · Air compressor");
        Assert.Contains("Door: Open", texts);
        Assert.Contains("Door: Closed", texts);
        Assert.Contains("Door turned on · Lights", texts);
        Assert.Contains("Auto-off turned off after 4 h · Lights", texts);
        Assert.Contains("Schedule turned on · Ventilation", texts);
        Assert.Contains("Schedule turned off · Outdoor light", texts);
        Assert.DoesNotContain(texts, t => t.Contains("Safety", StringComparison.Ordinal) || t.Contains("couldn't", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-11-11T11:00:00Z")] // Wednesday noon
    [InlineData("2026-11-09T05:30:00Z")] // Monday 06:30, before anyone came in
    [InlineData("2026-11-09T06:30:00Z")] // Monday 07:30, during the ventilation's window
    [InlineData("2026-11-14T17:00:00Z")] // Saturday 18:00, during the outdoor light's
    [InlineData("2026-11-15T23:30:00Z")] // Monday 00:30
    [InlineData("2026-03-30T10:00:00Z")] // after the clocks went forward
    [InlineData("2026-10-26T10:00:00Z")] // after the clocks went back
    [InlineData("2026-10-25T01:30:00Z")] // during the hour that comes twice
    public void Should_LeaveEverythingOff_AndTheDoorOpen_AtTheEnd(string now)
    {
        var nowUtc = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        var ids = new WorkshopIds(1, 1, 11, 13, 14, 15, 16, 21, 31, 32);

        var events = WorkshopTemplate.Events(ids, """{"steps":["Fan","Heat"],"delays":[3]}""", Stockholm, nowUtc);

        Assert.NotEmpty(events);
        Assert.Equal(events.OrderBy(e => e.TimestampUtc), events);
        Assert.All(events, e => Assert.InRange(e.TimestampUtc, nowUtc - WorkshopTemplate.History, nowUtc - WorkshopTemplate.EndsBefore));
        Assert.All(events, e => Assert.Equal(DateTimeKind.Utc, e.TimestampUtc.Kind));

        // Each relay goes on and off in turn, and ends off: the simulated board starts with every relay off.
        foreach (var relay in new[] { ids.Compressor, ids.Lights, ids.Ventilation, ids.Outdoor })
        {
            var switches = events.Where(e => e.RelayId == relay && e.GroupId is null).Select(e => e.Action).ToList();
            Assert.All(switches.Where((_, i) => i % 2 == 0), a => Assert.Equal(EventActions.On, a));
            Assert.All(switches.Where((_, i) => i % 2 == 1), a => Assert.Equal(EventActions.Off, a));
            Assert.True(switches.Count % 2 == 0, $"Relay {relay} is left on.");
        }

        // The group starts and stops in turn, and each stop's run-on ends.
        var group = events.Where(e => e.GroupId == ids.Group).Select(e => e.Action).ToList();
        Assert.Equal(
            Enumerable.Repeat<string[]>([EventActions.GroupOn, EventActions.GroupOff, EventActions.RunOnStart, EventActions.RunOnEnd], group.Count / 4).SelectMany(a => a),
            group);

        // The door opens and closes in turn, and was last opened: the simulated board starts with it open.
        var door = events.Where(e => e.InputId == ids.Door).Select(e => EventText.InputOn(e.Details)).ToList();
        Assert.NotEmpty(door);
        Assert.False(door[^1]);
        Assert.All(door.Zip(door.Skip(1)), p => Assert.NotEqual(p.First, p.Second));

        // A schedule's events only on its days and at its times: a window still running at the end isn't in them, so
        // the app's own start turns it on, as on any installation.
        foreach (var e in events.Where(e => e.Actor == EventActors.Schedule))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(e.TimestampUtc, Stockholm);
            if (e.RelayId == ids.Ventilation)
            {
                Assert.InRange(local.DayOfWeek, DayOfWeek.Monday, DayOfWeek.Friday);
                Assert.Equal(e.Action == EventActions.On ? 7 : 8, local.Hour);
            }
            else
            {
                Assert.Equal(ids.Outdoor, e.RelayId);
                Assert.Equal(e.Action == EventActions.On ? 16 : 22, local.Hour);
            }

            Assert.Equal(0, local.Minute);
        }
    }

    [Fact]
    public async Task Should_LeaveADatabase_TheVisitorTookFurther_AsItIs()
    {
        var database = Database(Wednesday);
        var chose = await PreparedAsync(database);
        await using (var db = Open(chose))
        {
            // The setup guide's choice of board adds it, with no model yet.
            db.Boards.Add(new Board { SortOrder = 1 });
            await db.SaveChangesAsync(Cancel);
        }

        var done = await PreparedAsync(database);
        await using (var db = Open(done))
        {
            (await db.Installations.SingleAsync(Cancel)).SetupDoneUtc = Wednesday.UtcDateTime;
            await db.SaveChangesAsync(Cancel);
        }

        Assert.False(await database.ApplyTemplateAsync(chose, Stockholm, Cancel));
        Assert.False(await database.ApplyTemplateAsync(done, Stockholm, Cancel));

        await using (var db = Open(chose))
        {
            Assert.Equal("", (await db.Boards.SingleAsync(Cancel)).Name);
            Assert.Empty(await db.Relays.ToListAsync(Cancel));
            Assert.Empty(await db.EventLog.ToListAsync(Cancel));
        }

        await using (var db = Open(done))
        {
            Assert.Empty(await db.Boards.ToListAsync(Cancel));
        }
    }

    [Fact]
    public async Task Should_FillItOnlyOnce()
    {
        var database = Database(Wednesday);
        var dataFolder = await PreparedAsync(database);

        Assert.True(await database.ApplyTemplateAsync(dataFolder, Stockholm, Cancel));
        Assert.False(await database.ApplyTemplateAsync(dataFolder, Stockholm, Cancel));

        await using var db = Open(dataFolder);
        Assert.Single(await db.RelayGroups.ToListAsync(Cancel));
        Assert.Equal(2, await db.Schedules.CountAsync(Cancel));
    }

    private static int Channel(List<Relay> relays, int relayId)
    {
        return relays.Single(r => r.Id == relayId).Channel;
    }

    private static string Text(EventLogEntry entry, Dictionary<string, string> names, Dictionary<int, string> relays)
    {
        var person = names.GetValueOrDefault(entry.Actor) ?? (EventActors.TryInput(entry.Actor, out _) ? "Door" : null);
        var view = EventText.For(
            entry,
            entry.RelayId is { } relayId ? relays[relayId] : null,
            person,
            entry.GroupId is null ? null : WorkshopTemplate.GroupName,
            entry.InputId is null ? null : "Door");
        return view.Target is null ? $"{view.Who} {view.What}" : $"{view.Who} {view.What} · {view.Target}";
    }

    private async Task<string> FilledAsync(DateTimeOffset now)
    {
        var database = Database(now);
        var dataFolder = await PreparedAsync(database);
        Assert.True(await database.ApplyTemplateAsync(dataFolder, Stockholm, Cancel));
        return dataFolder;
    }

    private async Task<string> PreparedAsync(AppCopyDatabase database)
    {
        var dataFolder = Path.Combine(_folder.DataRoot, VisitorCookie.NewId());
        Directory.CreateDirectory(dataFolder);
        await database.PrepareAsync(dataFolder, Cancel);
        return dataFolder;
    }

    private static AppCopyDatabase Database(DateTimeOffset now)
    {
        return new AppCopyDatabase(new FixedTime(now));
    }

    private static ContactorDbContext Open(string dataFolder)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataFolder, AppCopyDatabase.FileName), Pooling = false };
        return new ContactorDbContext(new DbContextOptionsBuilder<ContactorDbContext>().UseSqlite(connection.ToString()).Options);
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
