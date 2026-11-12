using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Events;

/// <summary>The names of those who switch relays: each person's display name by e-mail, and each input's name by its actor.</summary>
public static class ActorNames
{
    public static async Task<Dictionary<string, string>> LoadAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        foreach (var input in await db.Inputs.AsNoTracking().Select(i => new { i.Id, i.Name }).ToListAsync(cancellationToken))
        {
            names[EventActors.ForInput(input.Id)] = input.Name;
        }

        return names;
    }
}
