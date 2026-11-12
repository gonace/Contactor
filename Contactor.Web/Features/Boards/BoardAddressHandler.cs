using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// The relay board's address and login, set on the Settings screen so they can change without touching the
/// Raspberry Pi. Saved in the database; until someone saves them, the board's own settings apply (e.g.
/// <c>Vm201:Host</c>, <c>Port</c>, <c>Username</c> and <c>Password</c>).
/// </summary>
public sealed class BoardAddressHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly IBoardConnection _connection;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly GroupSequencer? _groups;

    public BoardAddressHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IBoardConnection connection,
        LocalClock clock,
        RelayChanges changes,
        GroupSequencer? groups = null)
    {
        _dbFactory = dbFactory;
        _connection = connection;
        _clock = clock;
        _changes = changes;
        _groups = groups;
    }

    /// <summary>The address in use: the saved one, or the settings' when none is saved.</summary>
    public async Task<BoardEndpoint> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.BoardSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return saved is null ? _connection.DefaultEndpoint : new BoardEndpoint(saved.Host, saved.Port);
    }

    /// <summary>The board's login in use: the saved one, or the settings' when none is saved. Shown without its password.</summary>
    public async Task<BoardLoginView> GetLoginAsync(CancellationToken cancellationToken)
    {
        var login = await LoginInUseAsync(cancellationToken);
        return new BoardLoginView(login.Username, login.Password.Length > 0);
    }

    /// <summary>Points the client at the saved address and login, if any. Called before the first connection.</summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        _connection.SetEndpoint(await GetAsync(cancellationToken));
        _connection.SetLogin(await LoginInUseAsync(cancellationToken));
    }

    /// <summary>
    /// Saves the board's login and reconnects with it at once. <paramref name="newPassword"/> null or empty keeps the
    /// password there is (the screen never shows it); <paramref name="none"/> saves a board without a login. Logs
    /// that it changed, never what to. Returns null when saved (or unchanged), otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveLoginAsync(string? username, string? newPassword, bool none, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var current = await LoginInUseAsync(cancellationToken);
        var password = none ? "" : string.IsNullOrEmpty(newPassword) ? current.Password : newPassword;
        var login = new BoardLogin(none ? "" : username?.Trim() ?? "", password);
        if (_connection.CheckLogin(login) is { } error)
        {
            return error;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.BoardSettings.SingleOrDefaultAsync(cancellationToken);
        if (saved is null)
        {
            // The row also holds the address: start it with the one in use.
            var endpoint = _connection.Endpoint;
            saved = new BoardSetting { Host = endpoint.Host, Port = endpoint.Port };
            db.BoardSettings.Add(saved);
        }
        else if (saved.Username is not null && current.Equals(login))
        {
            return null;
        }

        saved.Username = login.Username;
        saved.Password = login.Password;
        if (!current.Equals(login))
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _clock.UtcNow,
                Actor = userEmail,
                Action = EventActions.BoardLoginChanged,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        _connection.SetLogin(login);
        _changes.Notify();
        return null;
    }

    private async Task<BoardLogin> LoginInUseAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.BoardSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var login = saved?.Username is not null ? new BoardLogin(saved.Username, saved.Password ?? "") : _connection.DefaultLogin;

        // The database only holds what the board accepted, and the settings are checked at start; a login saved for
        // another model of board that this one can't take falls back to none.
        return _connection.CheckLogin(login) is null ? login : BoardLogin.None;
    }

    /// <summary>
    /// Saves a new address, logs who changed it from what, and moves the client there at once. Another board may be
    /// at the new address, so the old board's relay and group runs (who turned them on, when, their auto-off, a
    /// start or stop under way) are cleared in the same save. Returns null when saved (or unchanged), otherwise the
    /// message to show.
    /// </summary>
    public async Task<string?> SaveAsync(string? host, int port, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (BoardEndpoint.TryCreate(host, port, out var error) is not { } endpoint)
        {
            return error;
        }

        // Under the groups' lock, so a group's step can't save the old board's run back over the cleared one.
        var moved = _groups is null
            ? await SaveAddressAsync(endpoint, userEmail, cancellationToken)
            : await _groups.ExclusiveAsync(() => SaveAddressAsync(endpoint, userEmail, cancellationToken), cancellationToken);
        if (moved)
        {
            _connection.SetEndpoint(endpoint);
            _changes.Notify();
        }

        return null;
    }

    // Returns whether anything was saved, so the client moves.
    private async Task<bool> SaveAddressAsync(BoardEndpoint endpoint, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.BoardSettings.SingleOrDefaultAsync(cancellationToken);
        var current = saved is null ? _connection.DefaultEndpoint : new BoardEndpoint(saved.Host, saved.Port);
        if (saved is not null && current == endpoint)
        {
            return false;
        }

        if (saved is null)
        {
            saved = new BoardSetting();
            db.BoardSettings.Add(saved);
        }

        saved.Host = endpoint.Host;
        saved.Port = endpoint.Port;
        if (current != endpoint)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _clock.UtcNow,
                Actor = userEmail,
                Action = EventActions.BoardAddressChanged,
                // A new installation's first address replaces none.
                Details = current.HasHost
                    ? JsonSerializer.Serialize(new { from = current.ToString(), to = endpoint.ToString() })
                    : JsonSerializer.Serialize(new { to = endpoint.ToString() }),
            });

            // A new installation's first address replaces no board, so there is nothing of an old one to clear.
            if (current.HasHost)
            {
                await ForgetRunsAsync(db, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The old board's runs say nothing about the relays at the new address: who turned one on and its auto-off would
    // otherwise apply to the new board's relay of the same number.
    private static async Task ForgetRunsAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        foreach (var runtime in await db.RelayRuntimes.ToListAsync(cancellationToken))
        {
            (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy) = (null, null, null);
        }

        foreach (var runtime in await db.GroupRuntimes.ToListAsync(cancellationToken))
        {
            runtime.Phase = GroupPhases.Idle;
            runtime.TargetLevel = 0;
            runtime.NextStepAtUtc = null;
            (runtime.PendingPosition, runtime.PendingOn, runtime.PendingSinceUtc) = (null, false, null);
            (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy) = (null, null, null);
        }
    }
}

/// <summary>The board's login as the screen shows it: the user name, and only whether there is a password.</summary>
public sealed record BoardLoginView(string Username, bool HasPassword);
