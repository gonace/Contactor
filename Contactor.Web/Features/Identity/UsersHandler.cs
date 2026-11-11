using Microsoft.EntityFrameworkCore;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Identity;

/// <summary>A person as the Inställningar screen lists them.</summary>
public sealed record UserView(string Email, string DisplayName);

/// <summary>
/// The people who have signed in, and their names: the app names a new person after their e-mail
/// ("alex.smith@…" becomes "Alex.smith"), and the Inställningar screen lets anyone give them a proper one.
/// </summary>
public sealed class UsersHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly RelayChanges _changes;

    public UsersHandler(IDbContextFactory<ContactorDbContext> dbFactory, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _changes = changes;
    }

    public async Task<IReadOnlyList<UserView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        return [.. users.OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(u => new UserView(u.Email, u.DisplayName))];
    }

    /// <summary>Gives someone a new name. Returns null when saved, otherwise the message to show.</summary>
    public async Task<string?> RenameAsync(string email, string? displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var name = displayName?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Skriv ett namn.";
        }

        if (name.Length > User.DisplayNameMaxLength)
        {
            return $"Namnet får vara högst {User.DisplayNameMaxLength} tecken.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            return "Personen finns inte längre.";
        }

        if (user.DisplayName != name)
        {
            user.DisplayName = name;
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }

        return null;
    }
}
