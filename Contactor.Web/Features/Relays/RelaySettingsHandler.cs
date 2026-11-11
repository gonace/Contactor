using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>What the Relay screen edits.</summary>
public sealed record RelaySettings(int Id, string Name, bool IsActive, int? MaxOnMinutes);

/// <summary>
/// Loads and saves a relay's name, whether it is active, and its auto-off limit.
/// </summary>
public sealed class RelaySettingsHandler
{
    /// <summary>The limits the screen offers: 1 h, 2 h, 4 h, and null for never.</summary>
    public static readonly IReadOnlyList<int?> Limits = [60, 120, 240, null];

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;

    public RelaySettingsHandler(IDbContextFactory<ContactorDbContext> dbFactory, LocalClock clock, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
    }

    /// <summary>The relay's settings, or null for an id the board doesn't have.</summary>
    public async Task<RelaySettings?> GetAsync(int relayId, CancellationToken cancellationToken)
    {
        if (relayId is < 1 or > Relay.Count)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Relays.AsNoTracking()
            .Where(r => r.Id == relayId)
            .Select(r => new RelaySettings(r.Id, r.Name, r.IsActive, r.MaxOnMinutes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the settings and logs a new name and a change to active. A new limit also moves the planned auto-off
    /// of a relay a person turned on and that is on right now, counted from when it was turned on: whoever picks
    /// "1 h" means it should not run longer than that. If that time has passed, auto-off turns it off at its next
    /// check. A relay a schedule turned on is left alone.
    /// </summary>
    /// <returns>Null when saved, otherwise the message to show.</returns>
    public async Task<string?> SaveAsync(RelaySettings settings, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!Limits.Contains(settings.MaxOnMinutes))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MaxOnMinutes, "Not one of the offered limits.");
        }

        var name = settings.Name.Trim();
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (name.Length > Relay.NameMaxLength)
        {
            return $"The name can be at most {Relay.NameMaxLength} characters.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var relay = await db.Relays.SingleOrDefaultAsync(r => r.Id == settings.Id, cancellationToken);
        if (relay is null)
        {
            return "The relay doesn't exist.";
        }

        var now = _clock.UtcNow;
        if (relay.Name != name)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relay.Id,
                Actor = userEmail,
                Action = EventActions.Rename,
                Details = JsonSerializer.Serialize(new { from = relay.Name, to = name }),
            });
            relay.Name = name;
        }

        if (relay.IsActive != settings.IsActive)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relay.Id,
                Actor = userEmail,
                Action = settings.IsActive ? EventActions.Activate : EventActions.Deactivate,
            });
            relay.IsActive = settings.IsActive;
        }

        if (relay.MaxOnMinutes != settings.MaxOnMinutes)
        {
            relay.MaxOnMinutes = settings.MaxOnMinutes;
            var runtime = await db.RelayRuntimes.SingleOrDefaultAsync(r => r.RelayId == relay.Id, cancellationToken);
            // A relay a schedule turned on has no auto-off: the schedule's OffTime is its end.
            if (runtime?.OnSinceUtc is { } since && runtime.TurnedOnBy != EventActors.Schedule)
            {
                runtime.AutoOffAtUtc = settings.MaxOnMinutes is { } minutes ? since.AddMinutes(minutes) : null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    /// <summary>
    /// Whether picking <paramref name="maxOnMinutes"/> would mean auto-off is already overdue for the relay with
    /// <paramref name="runtime"/>, so the screen can say it will turn off on save. Never for a relay a schedule
    /// turned on.
    /// </summary>
    public static bool WouldTurnOffAtOnce(RelayRuntime? runtime, int? maxOnMinutes, DateTime nowUtc)
    {
        return runtime is { OnSinceUtc: { } since } && runtime.TurnedOnBy != EventActors.Schedule
            && maxOnMinutes is { } minutes && since.AddMinutes(minutes) <= nowUtc;
    }
}
