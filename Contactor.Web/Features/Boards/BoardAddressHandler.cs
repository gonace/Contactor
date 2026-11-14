using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// A relay board's name, address and login, set on the Settings screen so they can change without touching the
/// Raspberry Pi. Saved on the board's row (<see cref="Board"/>); until someone saves them, the board's own settings
/// apply: for the first board those in its model's section (e.g. <c>Vm201:Host</c>, <c>Port</c>, <c>Username</c> and
/// <c>Password</c>), for any other the model's default port and no address. Each by the board's row id.
/// </summary>
public sealed class BoardAddressHandler
{
    /// <summary>What saving says before a board is chosen: there is nothing to save it for.</summary>
    public const string NoBoardMessage = "No relay board has been chosen yet.";

    // Before the app runs a board on the row: none.
    private static readonly BoardEndpoint NoEndpoint = new("", 0);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;

    public BoardAddressHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        LocalClock clock,
        RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _clock = clock;
        _changes = changes;
    }

    /// <summary>The board's name, or null when there is no such board.</summary>
    public async Task<string?> GetNameAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Boards.Where(b => b.Id == boardId).Select(b => b.Name).SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the board's name (trimmed, at most <see cref="Board.NameMaxLength"/> characters), which the screens show at
    /// once. Returns null when saved (or unchanged), otherwise the message to show.
    /// </summary>
    public async Task<string?> RenameAsync(int boardId, string? name, CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (name.Length > Board.NameMaxLength)
        {
            return $"The name can be at most {Board.NameMaxLength} characters.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await RowAsync(db, boardId, cancellationToken) is not { } board)
        {
            return NoBoardMessage;
        }

        if (board.Name == name)
        {
            return null;
        }

        board.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        if (_registry.Find(boardId) is { } live)
        {
            live.Name = name;
        }

        _changes.Notify();
        return null;
    }

    /// <summary>The board's address in use: the saved one, or the settings' when none is saved.</summary>
    public async Task<BoardEndpoint> GetAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return BoardSetup.SavedEndpoint(await RowAsync(db, boardId, cancellationToken)) ?? _registry.Find(boardId)?.DefaultEndpoint ?? NoEndpoint;
    }

    /// <summary>The board's login in use: the saved one, or the settings' when none is saved. Shown without its password.</summary>
    public async Task<BoardLoginView> GetLoginAsync(int boardId, CancellationToken cancellationToken)
    {
        var login = await LoginInUseAsync(boardId, cancellationToken);
        return new BoardLoginView(login.Username, login.Password.Length > 0);
    }

    /// <summary>
    /// Saves the board's login and reconnects with it at once. <paramref name="newPassword"/> null or empty keeps the
    /// password there is (the screen never shows it); <paramref name="none"/> saves a board without a login. Logs
    /// that it changed, never what to. Returns null when saved (or unchanged), otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveLoginAsync(int boardId, string? username, string? newPassword, bool none, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (_registry.Find(boardId) is not { } connection)
        {
            return NoBoardMessage;
        }

        var current = await LoginInUseAsync(boardId, cancellationToken);
        var password = none ? "" : string.IsNullOrEmpty(newPassword) ? current.Password : newPassword;
        var login = new BoardLogin(none ? "" : username?.Trim() ?? "", password);
        if (connection.CheckLogin(login) is { } error)
        {
            return error;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await RowAsync(db, boardId, cancellationToken) is not { } board)
        {
            return NoBoardMessage;
        }

        if (BoardSetup.SavedEndpoint(board) is null)
        {
            // A login saved in the app keeps the address in use with it, as the settings' address and login go together.
            var endpoint = connection.Endpoint;
            board.Host = endpoint.Host;
            board.Port = endpoint.Port;
        }
        else if (board.Username is not null && current.Equals(login))
        {
            return null;
        }

        board.Username = login.Username;
        board.Password = login.Password;
        if (!current.Equals(login))
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _clock.UtcNow,
                BoardId = board.Id,
                Actor = userEmail,
                Action = EventActions.BoardLoginChanged,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        connection.SetLogin(login);
        _changes.Notify();
        return null;
    }

    private async Task<BoardLogin> LoginInUseAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = BoardSetup.SavedLogin(await RowAsync(db, boardId, cancellationToken));

        // The database only holds what the board accepted, and the settings are checked at start; a login saved for
        // another model of board that this one can't take falls back to none.
        return _registry.Find(boardId)?.Active is { } active ? active.LoginFor(saved) : BoardLogin.None;
    }

    // The board's row, or null when there is none.
    private static Task<Board?> RowAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        return db.Boards.SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken);
    }

    /// <summary>
    /// Saves a new address, logs who changed it from what, and moves the client there at once.
    /// Returns null when saved (or unchanged), otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveAsync(int boardId, string? host, int port, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (BoardEndpoint.TryCreate(host, port, out var error) is not { } endpoint)
        {
            return error;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (_registry.Find(boardId) is not { } connection || await RowAsync(db, boardId, cancellationToken) is not { } board)
        {
            return NoBoardMessage;
        }

        var saved = BoardSetup.SavedEndpoint(board);
        var current = saved ?? connection.DefaultEndpoint;
        if (saved is not null && current == endpoint)
        {
            return null;
        }

        board.Host = endpoint.Host;
        board.Port = endpoint.Port;
        if (current != endpoint)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _clock.UtcNow,
                BoardId = board.Id,
                Actor = userEmail,
                Action = EventActions.BoardAddressChanged,
                // A new installation's first address replaces none.
                Details = current.HasHost
                    ? JsonSerializer.Serialize(new { from = current.ToString(), to = endpoint.ToString() })
                    : JsonSerializer.Serialize(new { to = endpoint.ToString() }),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        connection.SetEndpoint(endpoint);
        _changes.Notify();
        return null;
    }
}

/// <summary>The board's login as the screen shows it: the user name, and only whether there is a password.</summary>
public sealed record BoardLoginView(string Username, bool HasPassword);
