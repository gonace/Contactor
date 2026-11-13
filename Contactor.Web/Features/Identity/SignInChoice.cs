using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Which sign-in the app uses: the one saved with the installation (<see cref="Installation.SignInType"/>), or none
/// while a new installation is being set up. Without one, only the setup guide answers, to whoever has the setup
/// code.
/// </summary>
public sealed class SignInChoice
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private SignInMethod? _current;

    public SignInChoice(IDbContextFactory<ContactorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>The sign-in the app uses, or null while the installation is being set up.</summary>
    public SignInMethod? Current => Volatile.Read(ref _current);

    /// <summary>At start: reads the saved sign-in. Throws for one the catalogue doesn't have.</summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.Installations.Select(i => i.SignInType).SingleOrDefaultAsync(cancellationToken);
        var method = saved is null
            ? null
            : SignInCatalogue.Find(saved)
                ?? throw new InvalidOperationException(
                    $"The sign-in saved for this installation is \"{saved}\", which this version of the app doesn't know. Use one of: {SignInCatalogue.Ids}.");
        Volatile.Write(ref _current, method);
    }
}
