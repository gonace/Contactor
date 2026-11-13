using Contactor.Web.Features.Cloudflare;

namespace Contactor.Web.Features.Identity;

/// <summary>How a sign-in method tells who made a request.</summary>
public interface ISignInMethod
{
    /// <summary>The person's e-mail, normalized (<see cref="IdentityRules.NormalizeEmail"/>), or null: denied.</summary>
    Task<string?> AuthenticateAsync(HttpContext context);
}

/// <summary>A sign-in method the app supports, as the catalogue lists it.</summary>
/// <param name="Id">Its id, saved with the installation (<c>Installation.SignInType</c>). Lower case.</param>
/// <param name="Name">Its name as people know it, e.g. "Cloudflare Access".</param>
/// <param name="Create">Gets it from the app's services.</param>
public sealed record SignInMethod(string Id, string Name, Func<IServiceProvider, ISignInMethod> Create);

/// <summary>Every sign-in method the app supports. A new method is a class of its own and one line here.</summary>
public static class SignInCatalogue
{
    /// <summary>What an installation has before it chose one: Cloudflare Access, as every installation had.</summary>
    public const string DefaultId = CloudflareMethods.AccessId;

    public static IReadOnlyList<SignInMethod> All { get; } =
    [
        new(CloudflareMethods.AccessId, "Cloudflare Access", s => s.GetRequiredService<CloudflareAccess>()),
    ];

    /// <summary>The method with this id, ignoring case and spaces around it, or null.</summary>
    public static SignInMethod? Find(string? id)
    {
        var trimmed = id?.Trim() ?? "";
        return All.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The ids, for a message about one the app doesn't know.</summary>
    public static string Ids => string.Join(", ", All.Select(m => m.Id));
}
