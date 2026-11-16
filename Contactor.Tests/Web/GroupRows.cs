using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>Groups saved straight into the database while the app runs, e.g. with no board to check their relays are off.</summary>
internal static class GroupRows
{
    /// <summary>
    /// Saves the groups with an idle runtime each, in one transaction, as <c>GroupsHandler</c> saves a new group. The
    /// running group sequencer makes a runtime for a group it finds without one; in one transaction it never finds one,
    /// so the two never both add a runtime for the same group.
    /// </summary>
    public static async Task AddWithRuntimesAsync(this ContactorDbContext db, params RelayGroup[] groups)
    {
        ArgumentNullException.ThrowIfNull(db);
        var cancel = TestContext.Current.CancellationToken;
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        db.RelayGroups.AddRange(groups);
        await db.SaveChangesAsync(cancel);
        db.GroupRuntimes.AddRange(groups.Select(g => new GroupRuntime { GroupId = g.Id }));
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
    }
}
