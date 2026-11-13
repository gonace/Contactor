using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Contactor.Web.Persistence;

/// <summary>
/// Raises <see cref="RelayRuntime.Version"/> by one on every save of a changed run, so no writer has to remember to.
/// The save then only goes through if the row still has the version it was read with (SQLite has no row version of
/// its own). A bulk update (<c>ExecuteUpdate</c>) doesn't pass through here and raises it itself.
/// </summary>
public sealed class RelayRunVersionInterceptor : SaveChangesInterceptor
{
    public static RelayRunVersionInterceptor Instance { get; } = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Raise(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Raise(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Raise(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<RelayRuntime>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version = entry.Property(r => r.Version).OriginalValue + 1;
            }
        }
    }
}
