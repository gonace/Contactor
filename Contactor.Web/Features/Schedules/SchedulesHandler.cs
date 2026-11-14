using System.Text.Json;
using System.Text.Json.Serialization;
using Contactor.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Schedules;

/// <summary>
/// A schedule as the screens show and edit it; <see cref="Id"/> is 0 for a new one. It is a relay's
/// (<see cref="RelayId"/>) or a group's (<see cref="GroupId"/>).
/// </summary>
public sealed record ScheduleModel(int Id, int? RelayId, ScheduleDays Days, TimeOnly OnTime, TimeOnly OffTime, bool IsEnabled, int? GroupId = null);

/// <summary>
/// Creates, changes, pauses and deletes schedules, and logs each change with who made it.
/// </summary>
public sealed class SchedulesHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly IRelayBoard _board;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;

    public SchedulesHandler(IDbContextFactory<ContactorDbContext> dbFactory, IRelayBoard board, LocalClock clock, RelayChanges changes)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
    }

    /// <summary>A relay's schedules.</summary>
    public async Task<IReadOnlyList<ScheduleModel>> ListAsync(int relayId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return InWeekOrder(await db.Schedules.AsNoTracking().Where(s => s.RelayId == relayId).ToListAsync(cancellationToken));
    }

    /// <summary>A group's schedules.</summary>
    public async Task<IReadOnlyList<ScheduleModel>> ListForGroupAsync(int groupId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return InWeekOrder(await db.Schedules.AsNoTracking().Where(s => s.GroupId == groupId).ToListAsync(cancellationToken));
    }

    private static List<ScheduleModel> InWeekOrder(List<Schedule> schedules)
    {
        // Ordered by the first day and then the time, like a week on a calendar (Monday first).
        return [.. schedules
            .Select(ToModel)
            .OrderBy(s => FirstDayIndex(s.Days))
            .ThenBy(s => s.OnTime)];
    }

    public async Task<ScheduleModel?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return schedule is null ? null : ToModel(schedule);
    }

    /// <summary>Checks a schedule before it is saved; null when it is fine, otherwise the message to show.</summary>
    public static string? Validate(ScheduleDays days, TimeOnly onTime, TimeOnly offTime)
    {
        if ((days & ScheduleDays.All) == ScheduleDays.None || (days & ~ScheduleDays.All) != ScheduleDays.None)
        {
            return "Choose at least one day.";
        }

        return onTime == offTime ? "Turning on and turning off can't be at the same time." : null;
    }

    /// <summary>Adds a schedule (Id 0) or changes one. Returns null when saved, otherwise the message to show.</summary>
    public async Task<string?> SaveAsync(ScheduleModel model, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var onTime = WholeMinute(model.OnTime);
        var offTime = WholeMinute(model.OffTime);
        if (Validate(model.Days, onTime, offTime) is { } error)
        {
            return error;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        Schedule schedule;
        string action;
        if (model.Id == 0)
        {
            if ((model.RelayId is null) == (model.GroupId is null))
            {
                return "Choose a relay or a group.";
            }

            if (model.GroupId is { } groupId)
            {
                if (!await db.RelayGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
                {
                    return "The group doesn't exist.";
                }
            }
            else
            {
                var count = _board.Capabilities.RelayCount;
                if (!await db.Relays.AnyAsync(r => r.Id == model.RelayId && r.Channel <= count, cancellationToken))
                {
                    return "The relay doesn't exist.";
                }

                // A relay in a group runs with its group; its own schedule would start it out of order.
                if (await db.RelayGroupMembers.AnyAsync(m => m.RelayId == model.RelayId, cancellationToken))
                {
                    return "This relay is in a group. Add the schedule to the group.";
                }
            }

            schedule = new Schedule { RelayId = model.RelayId, GroupId = model.GroupId, IsEnabled = true, CreatedBy = userEmail };
            db.Schedules.Add(schedule);
            action = EventActions.ScheduleAdd;
        }
        else
        {
            var existing = await db.Schedules.SingleOrDefaultAsync(s => s.Id == model.Id, cancellationToken);
            if (existing is null)
            {
                return "The schedule no longer exists.";
            }

            if (existing.Days == model.Days && existing.OnTime == onTime && existing.OffTime == offTime)
            {
                return null;
            }

            schedule = existing;
            action = EventActions.ScheduleEdit;
        }

        schedule.Days = model.Days;
        schedule.OnTime = onTime;
        schedule.OffTime = offTime;
        await LogAsync(db, schedule, userEmail, action, enabled: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    /// <summary>Pauses or resumes a schedule; false when it no longer exists.</summary>
    public async Task<bool> SetEnabledAsync(int id, bool isEnabled, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null)
        {
            return false;
        }

        if (schedule.IsEnabled != isEnabled)
        {
            schedule.IsEnabled = isEnabled;
            await LogAsync(db, schedule, userEmail, EventActions.ScheduleEdit, isEnabled, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }

        return true;
    }

    public async Task DeleteAsync(int id, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null)
        {
            return;
        }

        db.Schedules.Remove(schedule);
        await LogAsync(db, schedule, userEmail, EventActions.ScheduleDelete, enabled: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
    }

    private async Task LogAsync(ContactorDbContext db, Schedule schedule, string userEmail, string action, bool? enabled, CancellationToken cancellationToken)
    {
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = _clock.UtcNow,
            BoardId = await db.FirstBoardIdAsync(cancellationToken),
            RelayId = schedule.RelayId,
            GroupId = schedule.GroupId,
            Actor = userEmail,
            Action = action,
            Details = JsonSerializer.Serialize(new ScheduleDetails(
                (int)schedule.Days,
                ScheduleText.Time(schedule.OnTime),
                ScheduleText.Time(schedule.OffTime),
                enabled)),
        });
    }

    // Times are kept to the minute; seconds from a browser's time field would never match a tick.
    private static TimeOnly WholeMinute(TimeOnly time)
    {
        return new TimeOnly(time.Hour, time.Minute);
    }

    private static int FirstDayIndex(ScheduleDays days)
    {
        for (var i = 0; i < ScheduleText.Days.Count; i++)
        {
            if (days.HasFlag(ScheduleText.Days[i].Day))
            {
                return i;
            }
        }

        return ScheduleText.Days.Count;
    }

    private static ScheduleModel ToModel(Schedule schedule)
    {
        return new ScheduleModel(schedule.Id, schedule.RelayId, schedule.Days, schedule.OnTime, schedule.OffTime, schedule.IsEnabled, schedule.GroupId);
    }
}

/// <summary>The Details of a schedule event, so the log can say which schedule it was even after it is deleted.</summary>
/// <param name="Days">The <see cref="ScheduleDays"/> mask.</param>
/// <param name="On">"HH:mm".</param>
/// <param name="Off">"HH:mm".</param>
/// <param name="Enabled">Set when the schedule was paused (false) or resumed (true).</param>
public sealed record ScheduleDetails(
    [property: JsonPropertyName("days")] int Days,
    [property: JsonPropertyName("on")] string On,
    [property: JsonPropertyName("off")] string Off,
    [property: JsonPropertyName("enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled);
