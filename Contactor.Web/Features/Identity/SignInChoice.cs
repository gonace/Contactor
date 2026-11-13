using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Which sign-in the app uses: the one saved with the installation (<see cref="Installation.SignInType"/>), or none
/// while a new installation is being set up. Without one, only the setup guide answers, to whoever has the setup
/// code.
/// </summary>
public sealed class SignInChoice : IDisposable
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
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

    /// <summary>
    /// The setup guide's choice: saves it and uses it from the next request on, which ends setup mode. One choice at a
    /// time (two tabs of the guide), so what is saved is what is used.
    /// </summary>
    public async Task ChooseAsync(SignInMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var installation = await db.Installations.SingleOrDefaultAsync(cancellationToken);
            if (installation is null)
            {
                installation = new Installation();
                db.Installations.Add(installation);
            }

            installation.SignInType = method.Id;
            await db.SaveChangesAsync(cancellationToken);
            Volatile.Write(ref _current, method);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
