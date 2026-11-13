using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Web;

public sealed class CloudflareAccessTokenValidatorTests : IDisposable
{
    private readonly AccessTokens _tokens = new();
    private readonly FixedTimeProvider _time = new(AccessTokens.Now);
    private readonly CertificatesHandler _handler;
    private readonly ServiceProvider _services;
    private readonly CloudflareSettings _settings;
    private readonly CloudflareAccessTokenValidator _validator;

    public CloudflareAccessTokenValidatorTests()
    {
        _handler = new CertificatesHandler(_tokens.Certificates);
        var services = new ServiceCollection();
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);
        _services = services.BuildServiceProvider();
        // The team and application from the app's settings, as before the setup guide saved any.
        _settings = new CloudflareSettings(
            new NoDatabase(),
            Options.Create(new TunnelOptions()),
            Options.Create(new CloudflareAccessOptions { TeamDomain = AccessTokens.TeamDomain, Audience = AccessTokens.Audience }),
            NullLogger<CloudflareSettings>.Instance);
        var keys = new CloudflareAccessKeys(
            _services.GetRequiredService<IHttpClientFactory>(),
            _settings,
            _time,
            NullLogger<CloudflareAccessKeys>.Instance);
        _validator = new CloudflareAccessTokenValidator(keys, _settings, _time, NullLogger<CloudflareAccessTokenValidator>.Instance);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReturnNormalizedEmail_WhenTokenIsValid()
    {
        var email = await _validator.ValidateAsync(_tokens.Create(" Jane@Example.COM "), Cancel);

        Assert.Equal("jane@example.com", email);
        Assert.Equal(new Uri("https://team.example.com/cdn-cgi/access/certs"), _handler.LastRequestUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task Should_Reject_WhenTokenIsMissingOrMalformed(string? token)
    {
        Assert.Null(await _validator.ValidateAsync(token, Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenTokenIsHuge()
    {
        var token = new string('a', CloudflareAccessTokenValidator.MaxTokenLength + 1);

        Assert.Null(await _validator.ValidateAsync(token, Cancel));
        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_Reject_WhenAudienceIsAnotherApplication()
    {
        Assert.Null(await _validator.ValidateAsync(_tokens.Create(audience: "another-app"), Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenIssuerIsAnotherTeam()
    {
        Assert.Null(await _validator.ValidateAsync(_tokens.Create(issuer: "https://other.cloudflareaccess.com"), Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenTokenHasExpired()
    {
        var token = _tokens.Create(notBefore: AccessTokens.Now.AddHours(-2), expires: AccessTokens.Now.AddMinutes(-2));

        Assert.Null(await _validator.ValidateAsync(token, Cancel));
    }

    [Fact]
    public async Task Should_Accept_WhenExpiredWithinClockSkew()
    {
        var token = _tokens.Create(notBefore: AccessTokens.Now.AddHours(-1), expires: AccessTokens.Now.AddSeconds(-30));

        Assert.NotNull(await _validator.ValidateAsync(token, Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenTokenIsNotYetValid()
    {
        var token = _tokens.Create(notBefore: AccessTokens.Now.AddMinutes(5), expires: AccessTokens.Now.AddHours(1));

        Assert.Null(await _validator.ValidateAsync(token, Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenSignedWithAnotherKey()
    {
        using var stranger = new AccessTokens(keyId: "key-1");

        Assert.Null(await _validator.ValidateAsync(stranger.Create(), Cancel));
    }

    [Fact]
    public async Task Should_Reject_WhenTokenIsNotRs256()
    {
        Assert.Null(await _validator.ValidateAsync(_tokens.Create(algorithm: SecurityAlgorithms.RsaSha512), Cancel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("jane")]
    [InlineData("@example.com")]
    public async Task Should_Reject_WhenTokenHasNoUsableEmail(string? email)
    {
        Assert.Null(await _validator.ValidateAsync(_tokens.Create(email), Cancel));
    }

    [Fact]
    public async Task Should_FetchKeysOnce_ForManyTokens()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await _validator.ValidateAsync(_tokens.Create(), Cancel));
        }

        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_FetchKeysAgain_AfterAnHour()
    {
        await _validator.ValidateAsync(_tokens.Create(), Cancel);

        _time.Now = AccessTokens.Now + CloudflareAccessKeys.CacheDuration;
        var token = _tokens.Create(notBefore: _time.Now.AddMinutes(-1), expires: _time.Now.AddHours(1));
        await _validator.ValidateAsync(token, Cancel);

        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_AcceptRotatedKey_ByFetchingKeysAgain()
    {
        Assert.NotNull(await _validator.ValidateAsync(_tokens.Create(), Cancel));
        using var rotated = new AccessTokens(keyId: "key-2");
        _handler.Certificates = rotated.Certificates;
        _time.Now = AccessTokens.Now + CloudflareAccessKeys.MinRefreshInterval;

        var email = await _validator.ValidateAsync(rotated.Create(notBefore: _time.Now.AddMinutes(-1)), Cancel);

        Assert.Equal("jane@example.com", email);
        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_NotFetchKeysForEveryUnknownKey()
    {
        await _validator.ValidateAsync(_tokens.Create(), Cancel);
        using var stranger = new AccessTokens(keyId: "unknown");

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await _validator.ValidateAsync(stranger.Create(), Cancel));
        }

        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_DenyEveryToken_WhenKeysCannotBeFetched()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        Assert.Null(await _validator.ValidateAsync(_tokens.Create(), Cancel));
    }

    [Fact]
    public async Task Should_KeepKnownKeys_WhenARefetchFails()
    {
        await _validator.ValidateAsync(_tokens.Create(), Cancel);
        _handler.StatusCode = HttpStatusCode.NotFound;
        _time.Now = AccessTokens.Now + CloudflareAccessKeys.CacheDuration;

        var token = _tokens.Create(notBefore: _time.Now.AddMinutes(-1), expires: _time.Now.AddHours(1));

        Assert.NotNull(await _validator.ValidateAsync(token, Cancel));
        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task Should_DenyEveryToken_WhenCertsResponseIsHuge()
    {
        _handler.Certificates = () => new string(' ', 70_000) + _tokens.Certificates();

        Assert.Null(await _validator.ValidateAsync(_tokens.Create(), Cancel));
    }

    public void Dispose()
    {
        _services.Dispose();
        _tokens.Dispose();
        _settings.Dispose();
    }

    // These tests never save settings: nothing reads the database.
    private sealed class NoDatabase : IDbContextFactory<ContactorDbContext>
    {
        public ContactorDbContext CreateDbContext() => throw new InvalidOperationException("No database in these tests.");
    }
}
