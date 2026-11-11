using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>Which events the Händelser screen shows.</summary>
public enum EventFilter
{
    All,
    People,
    Automatic,
}

/// <summary>One row on the Händelser screen.</summary>
public sealed record EventRow(int Id, DateTime TimestampLocal, EventView View);

/// <summary>A day's events under its heading, e.g. "IDAG".</summary>
public sealed record EventDay(string Heading, IReadOnlyList<EventRow> Rows);

/// <summary>
/// Reads the event log for the Händelser screen, newest first, a page at a time.
/// </summary>
public sealed class EventsHandler
{
    public const int PageSize = 50;

    private static readonly string[] Weekdays = ["SÖNDAG", "MÅNDAG", "TISDAG", "ONSDAG", "TORSDAG", "FREDAG", "LÖRDAG"];
    private static readonly string[] Months = ["JAN", "FEB", "MARS", "APR", "MAJ", "JUNI", "JULI", "AUG", "SEP", "OKT", "NOV", "DEC"];

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;

    public EventsHandler(IDbContextFactory<ContactorDbContext> dbFactory, LocalClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    /// <summary>
    /// Up to <paramref name="count"/> events that come after the event <paramref name="beforeId"/> in the list (or
    /// the newest when null). The list is ordered by time, newest first, with the id breaking ties: some events are
    /// written after the time they record (a lost connection is logged 30 s later). Paging from the last row shown,
    /// rather than by offset, keeps a page from repeating rows when new events arrive meanwhile.
    /// </summary>
    /// <param name="relayId">Only this relay's events, or every event when null.</param>
    /// <param name="groupId">Only this group's events, or every event when null.</param>
    public async Task<IReadOnlyList<EventRow>> GetAsync(
        EventFilter filter,
        int? beforeId,
        int count,
        CancellationToken cancellationToken,
        int? relayId = null,
        int? groupId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.EventLog.AsNoTracking();
        if (beforeId is { } before)
        {
            var last = await db.EventLog.AsNoTracking().Where(e => e.Id == before).Select(e => (DateTime?)e.TimestampUtc).SingleOrDefaultAsync(cancellationToken);
            if (last is not { } lastTime)
            {
                return [];
            }

            query = query.Where(e => e.TimestampUtc < lastTime || (e.TimestampUtc == lastTime && e.Id < before));
        }

        if (relayId is { } onlyRelay)
        {
            query = query.Where(e => e.RelayId == onlyRelay);
        }

        if (groupId is { } onlyGroup)
        {
            query = query.Where(e => e.GroupId == onlyGroup);
        }

        // People are the actors with an e-mail address; everything else is automatic.
        query = filter switch
        {
            EventFilter.People => query.Where(e => e.Actor.Contains('@')),
            EventFilter.Automatic => query.Where(e => !e.Actor.Contains('@')),
            _ => query,
        };

        var entries = await query.OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id).Take(count).ToListAsync(cancellationToken);
        var relayNames = await db.Relays.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var personNames = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        var groupNames = await db.RelayGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        return [.. entries.Select(e => new EventRow(
            e.Id,
            _clock.ToLocal(e.TimestampUtc),
            EventText.For(
                e,
                e.RelayId is { } relayId ? relayNames.GetValueOrDefault(relayId) : null,
                personNames.GetValueOrDefault(e.Actor),
                e.GroupId is { } eventGroupId ? groupNames.GetValueOrDefault(eventGroupId) : null)))];
    }

    /// <summary>Groups rows (newest first) under one heading per local day.</summary>
    public IReadOnlyList<EventDay> GroupByDay(IEnumerable<EventRow> rows)
    {
        var today = _clock.Now.Date;
        return [.. rows
            .GroupBy(r => r.TimestampLocal.Date)
            .Select(g => new EventDay(Heading(g.Key, today), [.. g]))];
    }

    /// <summary>The day under an event on the Relä screen: "Idag", "I går", otherwise e.g. "Måndag 2 nov".</summary>
    public string DayLabel(DateTime local)
    {
        var heading = Heading(local, _clock.Now);
        return heading switch
        {
            "IDAG" => "Idag",
            "IGÅR" => "I går",
            _ => string.Concat(heading[..1], heading[1..].ToLower(CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>"IDAG", "IGÅR", otherwise e.g. "MÅNDAG 2 NOV" (with the year when it isn't this year).</summary>
    public static string Heading(DateTime day, DateTime today)
    {
        var days = (today.Date - day.Date).Days;
        if (days == 0)
        {
            return "IDAG";
        }

        if (days == 1)
        {
            return "IGÅR";
        }

        var heading = $"{Weekdays[(int)day.DayOfWeek]} {day.Day.ToString(CultureInfo.InvariantCulture)} {Months[day.Month - 1]}";
        return day.Year == today.Year ? heading : $"{heading} {day.Year.ToString(CultureInfo.InvariantCulture)}";
    }
}
