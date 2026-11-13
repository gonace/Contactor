using Contactor.Web.Features.Cloudflare;

namespace Contactor.Web.Features.Access;

/// <summary>How a way in keeps itself running, e.g. a tunnel the app starts.</summary>
public interface IAccessMethod
{
    /// <summary>Runs until cancelled, e.g. keeps a tunnel open. Returns at once when there is nothing to run.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>A way in the app supports, as the catalogue lists it.</summary>
/// <param name="Id">Its id, saved with the installation (<c>Installation.AccessType</c>). Lower case.</param>
/// <param name="Name">Its name as people know it, e.g. "Cloudflare".</param>
/// <param name="SignIns">The sign-in methods it works with, by id; the first is the one to recommend.</param>
/// <param name="Create">Gets it from the app's services.</param>
public sealed record AccessMethod(string Id, string Name, IReadOnlyList<string> SignIns, Func<IServiceProvider, IAccessMethod> Create);

/// <summary>Every way in the app supports. A new way in is a class of its own and one line here.</summary>
public static class AccessCatalogue
{
    public static IReadOnlyList<AccessMethod> All { get; } =
    [
        new(CloudflareMethods.TunnelId, "Cloudflare", [CloudflareMethods.AccessId], s => s.GetRequiredService<CloudflareTunnel>()),
    ];

    /// <summary>The way in with this id, ignoring case and spaces around it, or null.</summary>
    public static AccessMethod? Find(string? id)
    {
        var trimmed = id?.Trim() ?? "";
        return All.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The ids, for a message about one the app doesn't know.</summary>
    public static string Ids => string.Join(", ", All.Select(m => m.Id));
}
