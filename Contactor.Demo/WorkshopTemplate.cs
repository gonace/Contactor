using System.Text.Json;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Demo;

/// <summary>
/// The Workshop template (Issue #60): a VM201, as in the garage the app was made for, set up as if someone had been
/// through the setup guide and used it for three days. The board is the visitor's own simulated VM201, which the
/// copy's settings point at (<c>Vm201:Host</c> and <c>Vm201:Port</c>), the first board's address in the settings.
/// <list type="bullet">
/// <item>Group "Fan heater": relay 1 Fan → 3 s → relay 2 Heat. The fan runs on for 2 minutes after the heat goes
/// off; the group turns off after 2 h.</item>
/// <item>Relay 3 "Air compressor" turns off after 1 h, relay 4 "Lights" after 4 h.</item>
/// <item>Relay 5 "Ventilation": weekdays 07:00–08:00. Relay 6 "Outdoor light": every day 16:00–22:00.</item>
/// <item>Relays 7 and 8 not in use. Input 1 "Door": when it opens, Lights turn on.</item>
/// <item>Three days of events by Alex and Sam, ending before now, with every relay off at the end: the board, the
/// source of truth, starts with every relay off, and the door open.</item>
/// </list>
/// </summary>
public static class WorkshopTemplate
{
    public const string Id = "workshop";
    public const string BoardName = "Workshop";
    public const string GroupName = "Fan heater";
    public const string Alex = "alex@example.com";
    public const string Sam = "sam@example.com";

    /// <summary>How far back the events go.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(3);

    /// <summary>How long before now the events end at the latest.</summary>
    public static readonly TimeSpan EndsBefore = TimeSpan.FromMinutes(1);

    /// <summary>The fan's run-on after the heat goes off.</summary>
    public const int FanRunOnSec = 120;

    /// <summary>The wait between the fan and the heat.</summary>
    public const int HeatStartDelaySec = 3;

    // Each relay by channel: its name, whether it is in use, and its "Turn off automatically after". The fan and the
    // heat keep theirs, as making a group in the app leaves them; the group's own is what switches them.
    private static readonly (string Name, bool InUse, int? MaxOnMinutes)[] Relays =
    [
        ("Fan", true, Relay.DefaultMaxOnMinutes),
        ("Heat", true, Relay.DefaultMaxOnMinutes),
        ("Air compressor", true, 60),
        ("Lights", true, 240),
        ("Ventilation", true, Relay.DefaultMaxOnMinutes),
        ("Outdoor light", true, Relay.DefaultMaxOnMinutes),
        (Relay.DefaultName(7), false, Relay.DefaultMaxOnMinutes),
        (Relay.DefaultName(8), false, Relay.DefaultMaxOnMinutes),
    ];

    /// <summary>
    /// Whether the database is still as a new copy has it: the setup guide neither finished nor got as far as choosing
    /// the board. Only such a database is filled.
    /// </summary>
    public static async Task<bool> IsFreshAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return !await db.Installations.AnyAsync(i => i.SetupDoneUtc != null, cancellationToken)
            && !await db.Boards.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Fills a fresh database (<see cref="IsFreshAsync"/>) with the template, with the app's own database code: the
    /// board's rows as the setup guide's choice of board adds them, then the rest in one transaction, the setup guide's
    /// end last. Should that fail, the guide carries on from the board's choice. The app must not run meanwhile.
    /// </summary>
    /// <param name="services">The app's database services (<see cref="PersistenceServiceCollectionExtensions.AddPersistence"/>).</param>
    /// <param name="zone">The app's time zone (its <c>TimeZone</c> setting): the people's days and the schedules are in it.</param>
    /// <param name="nowUtc">Now: the events end before it.</param>
    public static async Task FillAsync(IServiceProvider services, TimeZoneInfo zone, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(zone);
        var model = BoardCatalogue.Find(BoardCatalogue.EarlierDefaultType)?.Model
            ?? throw new InvalidOperationException("The VM201 isn't in the board catalogue.");
        await services.SeedBoardAsync(model.Capabilities.RelayCount, model.Capabilities.InputCount, cancellationToken);

        var factory = services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var board = await db.FirstBoardAsync(cancellationToken)
            ?? throw new InvalidOperationException("The board's row wasn't added with its relays.");
        board.Type = model.Id;
        board.Name = BoardName;

        var relays = await db.Relays.Where(r => r.BoardId == board.Id).OrderBy(r => r.Channel).ToListAsync(cancellationToken);
        foreach (var relay in relays.Where(r => r.Channel <= Relays.Length))
        {
            (relay.Name, relay.IsActive, relay.MaxOnMinutes) = Relays[relay.Channel - 1];
        }

        var fan = Channel(relays, 1);
        var heat = Channel(relays, 2);
        var group = new RelayGroup
        {
            BoardId = board.Id,
            Name = GroupName,
            Icon = GroupIcons.Flame,
            MaxOnMinutes = RelayGroup.DefaultMaxOnMinutes,
            SortOrder = 1,
        };
        group.Members.Add(new RelayGroupMember { RelayId = fan.Id, Position = 1, RoleName = fan.Name, RunOnSec = FanRunOnSec });
        group.Members.Add(new RelayGroupMember { RelayId = heat.Id, Position = 2, RoleName = heat.Name, StartDelaySec = HeatStartDelaySec });
        db.RelayGroups.Add(group);

        var ventilation = new Schedule
        {
            RelayId = Channel(relays, 5).Id,
            Days = ScheduleDays.Weekdays,
            OnTime = new TimeOnly(7, 0),
            OffTime = new TimeOnly(8, 0),
            IsEnabled = true,
            CreatedBy = Alex,
        };
        var outdoor = new Schedule
        {
            RelayId = Channel(relays, 6).Id,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(16, 0),
            OffTime = new TimeOnly(22, 0),
            IsEnabled = true,
            CreatedBy = Alex,
        };
        db.Schedules.AddRange(ventilation, outdoor);

        var door = await db.Inputs.SingleAsync(i => i.BoardId == board.Id && i.Channel == 1, cancellationToken);
        door.Name = "Door";
        var lights = Channel(relays, 4);
        db.InputActions.Add(new InputAction { InputId = door.Id, RelayId = lights.Id, WhenOpened = true });

        foreach (var (email, name) in new[] { (Alex, "Alex"), (Sam, "Sam") })
        {
            if (!await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                db.Users.Add(new User { Email = email, DisplayName = name });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });

        // The scheduler carries on from now, with nothing to catch up, and the contact counts as lost just now: a short
        // gap, as after an upgrade, so the door keeps the time it opened at.
        var runtime = await db.BoardRuntimeAsync(board.Id, cancellationToken)
            ?? throw new InvalidOperationException("The board's runtime row wasn't added with its relays.");
        runtime.LastTickUtc = new DateTime(nowUtc.Ticks - (nowUtc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        runtime.LastContactUtc = nowUtc;

        var ids = new WorkshopIds(board.Id, group.Id, fan.Id, Channel(relays, 3).Id, lights.Id, ventilation.RelayId!.Value, outdoor.RelayId!.Value, door.Id, ventilation.Id, outdoor.Id);
        var members = group.Members
            .OrderBy(m => m.Position)
            .Select(m => new GroupMemberInfo(m.Position, m.RelayId, 0, m.RoleName, m.StartDelaySec, m.RunOnSec))
            .ToList();

        // One at a time, so the ids follow the times: the app takes the door's last event as its state.
        foreach (var entry in Events(ids, GroupPlanner.Chain(members), zone, nowUtc))
        {
            db.EventLog.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
        }

        var installation = await db.Installations.SingleOrDefaultAsync(cancellationToken);
        if (installation is null)
        {
            installation = new Installation();
            db.Installations.Add(installation);
        }

        installation.SetupDoneUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The template's events, oldest first: what the app logs when Alex and Sam use the workshop, the schedules run, the
    /// door opens and auto-off ends what was left on, as the app words them (<see cref="EventActions"/>). Only whole
    /// stretches (a relay on and off again, a group's start and stop with its run-on) from the last
    /// <see cref="History"/> that ended <see cref="EndsBefore"/> now, so nothing is on at the end; the door is left open
    /// at the end, as the simulated board starts with it.
    /// </summary>
    public static IReadOnlyList<EventLogEntry> Events(WorkshopIds ids, string chain, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone));
        var stretches = new List<List<EventLogEntry>>();
        for (var day = today.AddDays(-4); day <= today; day = day.AddDays(1))
        {
            stretches.AddRange(new Day(ids, chain, zone, day).Stretches());
        }

        var from = nowUtc - History;
        var to = nowUtc - EndsBefore;
        var events = stretches
            .Where(s => s[0].TimestampUtc >= from && s[^1].TimestampUtc <= to)
            .SelectMany(s => s)
            .Select((e, order) => (Entry: e, Order: order))
            .OrderBy(e => e.Entry.TimestampUtc)
            .ThenBy(e => e.Order)
            .Select(e => e.Entry)
            .ToList();

        // The door was left open after it was last opened.
        var lastDoor = events.FindLastIndex(e => e.Action == EventActions.InputChanged);
        if (lastDoor >= 0 && events[lastDoor].Details == DoorClosed)
        {
            events.RemoveAt(lastDoor);
        }

        return events;
    }

    private const string DoorOpened = """{"on":false}""";
    private const string DoorClosed = """{"on":true}""";

    private static Relay Channel(List<Relay> relays, int channel)
    {
        return relays.SingleOrDefault(r => r.Channel == channel)
            ?? throw new InvalidOperationException($"The board has no relay {channel}.");
    }

    // One day in the workshop, in stretches that each leave what they switched off again.
    private sealed class Day
    {
        private readonly WorkshopIds _ids;
        private readonly string _chain;
        private readonly TimeZoneInfo _zone;
        private readonly DateOnly _date;

        public Day(WorkshopIds ids, string chain, TimeZoneInfo zone, DateOnly date)
        {
            _ids = ids;
            _chain = chain;
            _zone = zone;
            _date = date;
        }

        public IEnumerable<List<EventLogEntry>> Stretches()
        {
            // Some variety from day to day, the same every time for one day.
            var variant = _date.DayNumber % 2;
            var compressor = _date.DayNumber % 3;
            switch (_date.DayOfWeek)
            {
                case DayOfWeek.Saturday:
                    yield return Door(At(9, 5), Sam, At(11, 15));
                    yield return Heating(Sam, At(9, 8), At(10, 40), autoOff: false);
                    yield return Switch(_ids.Compressor, Alex, At(9, 30), At(9, 48), autoOffMinutes: null);
                    break;
                case DayOfWeek.Sunday:
                    break;
                default:
                    yield return Schedule(_ids.Ventilation, _ids.VentilationSchedule, At(7, 0), At(8, 0));
                    yield return Door(At(7, 28), variant == 0 ? null : Sam, variant == 0 ? At(11, 28, 1) : At(11, 5));
                    yield return Heating(Alex, At(7, 31), variant == 0 ? At(9, 15) : At(9, 31), autoOff: variant == 1);
                    if (compressor == 0)
                    {
                        yield return Switch(_ids.Compressor, Sam, At(10, 12), At(10, 41), autoOffMinutes: null);
                    }
                    else if (compressor == 1)
                    {
                        yield return Switch(_ids.Compressor, Alex, At(13, 20), At(14, 20), autoOffMinutes: 60);
                    }

                    yield return Door(At(15, 52), Alex, At(16, 25));
                    break;
            }

            yield return Schedule(_ids.Outdoor, _ids.OutdoorSchedule, At(16, 0), At(22, 0));
        }

        // The door opens, which turns the lights on, and closes again; later someone turns the lights off, or (with
        // no one) auto-off after 4 h.
        private List<EventLogEntry> Door(DateTime opened, string? lightsOffBy, DateTime lightsOff)
        {
            var actor = EventActors.ForInput(_ids.Door);
            return
            [
                new() { TimestampUtc = opened, BoardId = _ids.Board, InputId = _ids.Door, Actor = EventActors.External, Action = EventActions.InputChanged, Details = DoorOpened },
                new() { TimestampUtc = opened.AddSeconds(1), BoardId = _ids.Board, RelayId = _ids.Lights, Actor = actor, Action = EventActions.On },
                new() { TimestampUtc = opened.AddMinutes(1), BoardId = _ids.Board, InputId = _ids.Door, Actor = EventActors.External, Action = EventActions.InputChanged, Details = DoorClosed },
                Off(_ids.Lights, lightsOffBy, lightsOff, lightsOffBy is null ? 240 : null),
            ];
        }

        // The fan heater: on in its order, then off by the person or after its 2 h, with the fan's run-on.
        private List<EventLogEntry> Heating(string by, DateTime on, DateTime off, bool autoOff)
        {
            var runOn = off.AddSeconds(1);
            return
            [
                new() { TimestampUtc = on, BoardId = _ids.Board, GroupId = _ids.Group, Actor = by, Action = EventActions.GroupOn, Details = _chain },
                new()
                {
                    TimestampUtc = off,
                    BoardId = _ids.Board,
                    GroupId = _ids.Group,
                    Actor = autoOff ? EventActors.AutoOff : by,
                    Action = EventActions.GroupOff,
                    Details = autoOff ? JsonSerializer.Serialize(new { afterMinutes = (int)Math.Round((off - on).TotalMinutes) }) : null,
                },
                new()
                {
                    TimestampUtc = runOn,
                    BoardId = _ids.Board,
                    GroupId = _ids.Group,
                    RelayId = _ids.Fan,
                    Actor = EventActors.System,
                    Action = EventActions.RunOnStart,
                    Details = JsonSerializer.Serialize(new { seconds = FanRunOnSec, role = "Fan" }),
                },
                new()
                {
                    TimestampUtc = runOn.AddSeconds(FanRunOnSec),
                    BoardId = _ids.Board,
                    GroupId = _ids.Group,
                    RelayId = _ids.Fan,
                    Actor = EventActors.System,
                    Action = EventActions.RunOnEnd,
                    Details = JsonSerializer.Serialize(new { role = "Fan" }),
                },
            ];
        }

        private List<EventLogEntry> Switch(int relayId, string by, DateTime on, DateTime off, int? autoOffMinutes)
        {
            return
            [
                new() { TimestampUtc = on, BoardId = _ids.Board, RelayId = relayId, Actor = by, Action = EventActions.On },
                Off(relayId, autoOffMinutes is null ? by : null, off, autoOffMinutes),
            ];
        }

        private List<EventLogEntry> Schedule(int relayId, int scheduleId, DateTime on, DateTime off)
        {
            var details = JsonSerializer.Serialize(new { scheduleId });
            return
            [
                new() { TimestampUtc = on, BoardId = _ids.Board, RelayId = relayId, Actor = EventActors.Schedule, Action = EventActions.On, Details = details },
                new() { TimestampUtc = off, BoardId = _ids.Board, RelayId = relayId, Actor = EventActors.Schedule, Action = EventActions.Off, Details = details },
            ];
        }

        // By a person, or by auto-off after the relay's limit.
        private EventLogEntry Off(int relayId, string? by, DateTime at, int? afterMinutes)
        {
            return new EventLogEntry
            {
                TimestampUtc = at,
                BoardId = _ids.Board,
                RelayId = relayId,
                Actor = by ?? EventActors.AutoOff,
                Action = EventActions.Off,
                Details = by is null ? JsonSerializer.Serialize(new { afterMinutes }) : null,
            };
        }

        // A time of the day in the app's zone, in UTC. The workshop's hours (07–22) never fall in a daylight saving
        // change, which every zone with one makes at night; a zone that skips the hour anyway moves it an hour on.
        private DateTime At(int hour, int minute, int second = 0)
        {
            var local = _date.ToDateTime(new TimeOnly(hour, minute, second), DateTimeKind.Unspecified);
            if (_zone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(local, _zone);
        }
    }
}

/// <summary>The rows the template's events are about.</summary>
public sealed record WorkshopIds(
    int Board,
    int Group,
    int Fan,
    int Compressor,
    int Lights,
    int Ventilation,
    int Outdoor,
    int Door,
    int VentilationSchedule,
    int OutdoorSchedule);
