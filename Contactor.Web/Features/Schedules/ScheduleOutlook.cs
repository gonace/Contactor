using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Schedules;

/// <summary>What the schedules say about a relay or a group right now.</summary>
/// <param name="RunningUntilUtc">When the run going on now ends (the latest end when runs overlap), or null.</param>
/// <param name="NextOnUtc">When a schedule next turns it on within the coming 24 h, or null.</param>
public sealed record RelayOutlook(DateTime? RunningUntilUtc, DateTime? NextOnUtc);

/// <summary>
/// Reads the schedules for the status lines: "On since 06:30 · schedule · off 08:00" and "Schedule: on tomorrow 06:30".
/// </summary>
public sealed class ScheduleOutlook
{
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;

    public ScheduleOutlook(IDbContextFactory<ContactorDbContext> dbFactory, LocalClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    /// <summary>By relay id.</summary>
    public async Task<IReadOnlyDictionary<int, RelayOutlook>> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.RelayId != null).ToListAsync(cancellationToken);
        return For(schedules, _clock.Zone, _clock.UtcNow);
    }

    /// <summary>By group id.</summary>
    public async Task<IReadOnlyDictionary<int, RelayOutlook>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.GroupId != null).ToListAsync(cancellationToken);
        return For(schedules, _clock.Zone, _clock.UtcNow, w => w.GroupId);
    }

    /// <summary>By relay id; a group's schedules are left out.</summary>
    public static IReadOnlyDictionary<int, RelayOutlook> For(IEnumerable<Schedule> schedules, TimeZoneInfo zone, DateTime nowUtc)
    {
        return For(schedules, zone, nowUtc, w => w.RelayId);
    }

    private static Dictionary<int, RelayOutlook> For(IEnumerable<Schedule> schedules, TimeZoneInfo zone, DateTime nowUtc, Func<ScheduleWindow, int?> keyOf)
    {
        var windows = ScheduleCalendar.Windows(schedules, zone, nowUtc, nowUtc + Horizon);
        return windows
            .Where(w => keyOf(w) is not null)
            .GroupBy(w => keyOf(w)!.Value)
            .ToDictionary(
                g => g.Key,
                g => new RelayOutlook(
                    g.Where(w => w.Contains(nowUtc)).Select(w => (DateTime?)w.OffUtc).Max(),
                    g.Where(w => w.OnUtc > nowUtc && w.OnUtc <= nowUtc + Horizon).Select(w => (DateTime?)w.OnUtc).Min()));
    }
}
