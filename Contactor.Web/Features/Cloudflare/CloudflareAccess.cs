using Contactor.Web.Features.Identity;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>Cloudflare Access as the sign-in: the person is who Cloudflare's signed token (<c>Cf-Access-Jwt-Assertion</c>) says.</summary>
public sealed class CloudflareAccess : ISignInMethod
{
    private readonly CloudflareAccessTokenValidator _validator;

    public CloudflareAccess(CloudflareAccessTokenValidator validator)
    {
        _validator = validator;
    }

    public Task<string?> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _validator.ValidateAsync(context.Request.Headers[CloudflareAccessTokenValidator.HeaderName], context.RequestAborted);
    }
}
