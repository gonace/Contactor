using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>Which events the Events screen shows.</summary>
public enum EventFilter
{
    All,
    People,
    Automatic,
}

/// <summary>One row on the Events screen.</summary>
public sealed record EventRow(int Id, DateTime TimestampLocal, EventView View);

/// <summary>A day's events under its heading, e.g. "TODAY".</summary>
public sealed record EventDay(string Heading, IReadOnlyList<EventRow> Rows);

/// <summary>
/// Reads the event log for the Events screen, newest first, a page at a time.
/// </summary>
public sealed class EventsHandler
{
    public const int PageSize = 50;

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

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
        int? groupId = null,
        int? inputId = null)
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

        if (inputId is { } onlyInput)
        {
            query = query.Where(e => e.InputId == onlyInput);
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
        var inputs = await db.Inputs.AsNoTracking().ToDictionaryAsync(i => i.Id, i => new InputWords(i.Name, i.ClosedText, i.OpenText), cancellationToken);

        return [.. entries.Select(e => new EventRow(
            e.Id,
            _clock.ToLocal(e.TimestampUtc),
            EventText.For(
                e,
                e.RelayId is { } relayId ? relayNames.GetValueOrDefault(relayId) : null,
                personNames.GetValueOrDefault(e.Actor),
                e.GroupId is { } eventGroupId ? groupNames.GetValueOrDefault(eventGroupId) : null,
                e.Action == EventActions.InputChanged ? inputs.GetValueOrDefault(e.InputId ?? 1) : null)))];
    }

    /// <summary>Groups rows (newest first) under one heading per local day.</summary>
    public IReadOnlyList<EventDay> GroupByDay(IEnumerable<EventRow> rows)
    {
        var today = _clock.Now.Date;
        return [.. rows
            .GroupBy(r => r.TimestampLocal.Date)
            .Select(g => new EventDay(Heading(g.Key, today), [.. g]))];
    }

    /// <summary>The day under an event on the Relay screen: "Today", "Yesterday", otherwise e.g. "Monday 2 Nov".</summary>
    public string DayLabel(DateTime local)
    {
        return Day(local, _clock.Now);
    }

    /// <summary>"TODAY", "YESTERDAY", otherwise e.g. "MONDAY 2 NOV" (with the year when it isn't this year).</summary>
    public static string Heading(DateTime day, DateTime today)
    {
        return Day(day, today).ToUpper(CultureInfo.InvariantCulture);
    }

    private static string Day(DateTime day, DateTime today)
    {
        var days = (today.Date - day.Date).Days;
        if (days == 0)
        {
            return "Today";
        }

        if (days == 1)
        {
            return "Yesterday";
        }

        var text = $"{day.DayOfWeek} {day.Day.ToString(CultureInfo.InvariantCulture)} {Months[day.Month - 1]}";
        return day.Year == today.Year ? text : $"{text} {day.Year.ToString(CultureInfo.InvariantCulture)}";
    }
}
