using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Contactor.Web.Features.Identity;

/// <summary>A signed-in person.</summary>
public sealed record SignedInUser(string Email, string DisplayName);

/// <summary>
/// Who is using this page. Scoped, so in Blazor Server it belongs to one circuit: it reads the user that
/// <see cref="SignInMiddleware"/> put on the request that started the circuit, since request headers are
/// gone once the circuit runs.
/// </summary>
public sealed class CurrentUser
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public CurrentUser(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    /// <summary>
    /// The user, or null if the request had none: only while a new installation is being set up, when the middleware
    /// lets the setup guide through to whoever typed the setup code.
    /// </summary>
    public async Task<SignedInUser?> GetAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var email = state.User.FindFirstValue(ClaimTypes.Email);
        var name = state.User.FindFirstValue(ClaimTypes.Name);
        return email is null || name is null ? null : new SignedInUser(email, name);
    }
}
