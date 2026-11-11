using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Validates the <c>Cf-Access-Jwt-Assertion</c> token: signed by Access with RS256, issued by our team domain, for
/// our application's AUD tag, and valid now. Returns the e-mail from the signed token, never from a plain header.
/// </summary>
public sealed partial class CloudflareAccessTokenValidator
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";

    // Access tokens are about 1 KB; anything far bigger is not one, so it is not parsed.
    public const int MaxTokenLength = 16 * 1024;

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly CloudflareAccessKeys _keys;
    private readonly CloudflareAccessOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CloudflareAccessTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new() { MaximumTokenSizeInBytes = MaxTokenLength };

    public CloudflareAccessTokenValidator(
        CloudflareAccessKeys keys,
        IOptions<CloudflareAccessOptions> options,
        TimeProvider timeProvider,
        ILogger<CloudflareAccessTokenValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keys = keys;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The normalized e-mail the token was issued to, or null when the token is not valid.</summary>
    public async Task<string?> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        var result = await ValidateWithKeysAsync(token, refresh: false, cancellationToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Access may have rotated its keys.
            result = await ValidateWithKeysAsync(token, refresh: true, cancellationToken);
        }

        if (!result.IsValid)
        {
            LogInvalidToken(result.Exception?.GetType().Name ?? "unknown");
            return null;
        }

        if (!result.Claims.TryGetValue("email", out var claim) || claim is not string email)
        {
            LogInvalidToken("no email claim");
            return null;
        }

        email = IdentityRules.NormalizeEmail(email);
        return IdentityRules.IsValidEmail(email) ? email : null;
    }

    private async Task<TokenValidationResult> ValidateWithKeysAsync(string token, bool refresh, CancellationToken cancellationToken)
    {
        var keys = await _keys.GetAsync(refresh, cancellationToken);
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = ClockSkew,

            // Judged against the injected clock, not the machine's, so tests and the app agree on "now".
            LifetimeValidator = (notBefore, expires, _, _) => IsWithinLifetime(notBefore, expires),
        };
        return await _handler.ValidateTokenAsync(token, parameters);
    }

    private bool IsWithinLifetime(DateTime? notBefore, DateTime? expires)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return expires is not null
            && now <= expires.Value.ToUniversalTime() + ClockSkew
            && (notBefore is null || now >= notBefore.Value.ToUniversalTime() - ClockSkew);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a Cloudflare Access token: {Reason}")]
    private partial void LogInvalidToken(string reason);
}
