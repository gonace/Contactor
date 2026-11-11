using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// Each person's "Debugging" checkbox on the Inställningar screen: whether they see the board's traffic as toasts and
/// get the Debug tab. Only that person; the others don't see it.
/// </summary>
public sealed class BoardTrafficSetting
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;

    public BoardTrafficSetting(IDbContextFactory<ContactorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Raised with the person's e-mail when their setting changes, so their other open pages follow.</summary>
    public event EventHandler<string>? Changed;

    public async Task<bool> IsOnAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => u.ShowBoardTraffic)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string email, bool on, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null || user.ShowBoardTraffic == on)
        {
            return;
        }

        user.ShowBoardTraffic = on;
        await db.SaveChangesAsync(cancellationToken);
        Changed?.Invoke(this, email);
    }
}
