using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Access;

/// <summary>
/// Which way in the app runs: the one saved with the installation (<see cref="Installation.AccessType"/>), or none
/// while a new installation hasn't chosen one.
/// </summary>
public sealed class AccessChoice
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private AccessMethod? _current;

    public AccessChoice(IDbContextFactory<ContactorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>The way in the app runs, or null before one is chosen.</summary>
    public AccessMethod? Current => Volatile.Read(ref _current);

    /// <summary>At start: reads the saved way in. Throws for one the catalogue doesn't have.</summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.Installations.Select(i => i.AccessType).SingleOrDefaultAsync(cancellationToken);
        var method = saved is null
            ? null
            : AccessCatalogue.Find(saved)
                ?? throw new InvalidOperationException(
                    $"The way in saved for this installation is \"{saved}\", which this version of the app doesn't know. Use one of: {AccessCatalogue.Ids}.");
        Volatile.Write(ref _current, method);
    }
}
