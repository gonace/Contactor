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
/// <param name="Description">What it is, in plain words, as the setup guide shows it.</param>
/// <param name="NotRecommended">Whether the guide tags it "Not recommended" and never recommends it.</param>
public sealed record SignInMethod(string Id, string Name, string Description, bool NotRecommended = false)
{
    /// <summary>Gets it from the app's services, where it is registered by its id (a test can put another there).</summary>
    public ISignInMethod Create(IServiceProvider services) => services.GetRequiredKeyedService<ISignInMethod>(Id);
}

/// <summary>Every sign-in method the app supports. A new method is a class of its own and one line here.</summary>
public static class SignInCatalogue
{
    /// <summary>No sign-in: everyone is the same person, <see cref="NoSignIn"/>.</summary>
    public const string NoneId = "none";

    public static IReadOnlyList<SignInMethod> All { get; } =
    [
        new(CloudflareMethods.AccessId, "Cloudflare Access", "A code by e-mail, through Cloudflare. Who may sign in is set in Cloudflare."),
        new(NoneId, "No sign-in", "Anyone who can open the app can switch the relays, and the events can't say who did it.", NotRecommended: true),
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
