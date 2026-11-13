using Contactor.Web.Features.Identity;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// Cloudflare Access as the sign-in: the person is who Cloudflare's signed token (<c>Cf-Access-Jwt-Assertion</c>)
/// says. In Development everyone is <c>CloudflareAccess:DevUserEmail</c> instead, when it is set.
/// </summary>
public sealed class CloudflareAccess : ISignInMethod
{
    private readonly CloudflareAccessOptions _options;
    private readonly CloudflareAccessTokenValidator _validator;

    public CloudflareAccess(IOptions<CloudflareAccessOptions> options, CloudflareAccessTokenValidator validator)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _validator = validator;
    }

    public Task<string?> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _options.UsesDevUser
            ? Task.FromResult<string?>(IdentityRules.NormalizeEmail(_options.DevUserEmail!))
            : _validator.ValidateAsync(context.Request.Headers[CloudflareAccessTokenValidator.HeaderName], context.RequestAborted);
    }
}
