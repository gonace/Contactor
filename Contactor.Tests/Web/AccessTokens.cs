using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Contactor.Tests.Web;

/// <summary>
/// Stands in for Cloudflare Access: an RSA signing key, its certs document, and tokens signed with it.
/// </summary>
internal sealed class AccessTokens : IDisposable
{
    public const string TeamDomain = "team.example.com";
    public const string Audience = "aud-0123456789";
    public const string Issuer = "https://" + TeamDomain;

    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);

    public AccessTokens(string keyId = "key-1")
    {
        KeyId = keyId;
    }

    public string KeyId { get; }

    public static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["CloudflareAccess:TeamDomain"] = TeamDomain,
        ["CloudflareAccess:Audience"] = Audience,
    };

    /// <summary>The certs document with this key's public half.</summary>
    public string Certificates()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{KeyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}
            """;
    }

    public string Create(
        string? email = "nils@example.com",
        string audience = Audience,
        string issuer = Issuer,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var claims = new Dictionary<string, object>();
        if (email is not null)
        {
            claims["email"] = email;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = (notBefore ?? Now.AddMinutes(-5)).UtcDateTime,
            NotBefore = (notBefore ?? Now.AddMinutes(-5)).UtcDateTime,
            Expires = (expires ?? Now.AddHours(1)).UtcDateTime,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, algorithm),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}

/// <summary>A clock that always says <see cref="Now"/>.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}

/// <summary>Serves the Access certs endpoint and counts requests.</summary>
internal sealed class CertificatesHandler : HttpMessageHandler
{
    private int _requestCount;

    public CertificatesHandler(Func<string> certificates)
    {
        Certificates = certificates;
    }

    public Func<string> Certificates { get; set; }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        LastRequestUri = request.RequestUri;
        var response = new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(StatusCode == HttpStatusCode.OK ? Certificates() : "", Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }

    // The HTTP client factory disposes the handlers it uses. The base class holds nothing, so this one stays usable
    // for the next client the test creates.
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
