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
        Assert.Equal(new Uri("https://team.cloudflareaccess.com/cdn-cgi/access/certs"), _handler.LastRequestUri);
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

    // A request that comes while the first keys are on their way waits for them, rather than being denied with none.
    [Fact]
    public async Task Should_LetARequestWaitForTheKeys_WhileTheFirstAreFetched()
    {
        using var held = new HeldCertificates(_tokens.Certificates());
        using var keys = Keys(held, out var provider);
        await using var _ = provider;
        var access = new CloudflareAccessSettings(AccessTokens.TeamDomain, AccessTokens.Audience);

        var first = Task.Run(() => keys.GetAsync(access, refresh: false, Cancel), Cancel);
        Assert.True(held.Fetching.Wait(TimeSpan.FromSeconds(10), Cancel), "The first fetch never started.");
        var second = keys.GetAsync(access, refresh: false, Cancel);
        Assert.False(second.IsCompleted, "The second request didn't wait for the keys.");
        held.Answer.Set();

        Assert.Equal(_tokens.KeyId, Assert.Single(await first).KeyId);
        Assert.Equal(_tokens.KeyId, Assert.Single(await second).KeyId);
        Assert.Equal(1, held.RequestCount);
    }

    // A fetch that fails still counts: the requests that waited for it take what it got, and don't each fetch again.
    [Fact]
    public async Task Should_FetchOnce_ForTheRequestsThatWaited_WhenTheFetchFails()
    {
        using var held = new HeldCertificates(_tokens.Certificates()) { StatusCode = HttpStatusCode.InternalServerError };
        using var keys = Keys(held, out var provider);
        await using var _ = provider;
        var access = new CloudflareAccessSettings(AccessTokens.TeamDomain, AccessTokens.Audience);

        var first = Task.Run(() => keys.GetAsync(access, refresh: false, Cancel), Cancel);
        Assert.True(held.Fetching.Wait(TimeSpan.FromSeconds(10), Cancel), "The first fetch never started.");
        var waiting = Enumerable.Range(0, 5).Select(_ => keys.GetAsync(access, refresh: true, Cancel)).ToList();
        held.Answer.Set();

        Assert.Empty(await first);
        Assert.All(await Task.WhenAll(waiting), Assert.Empty);
        Assert.Equal(1, held.RequestCount);
    }

    private CloudflareAccessKeys Keys(HttpMessageHandler handler, out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        provider = services.BuildServiceProvider();
        return new CloudflareAccessKeys(provider.GetRequiredService<IHttpClientFactory>(), _settings, _time, NullLogger<CloudflareAccessKeys>.Instance);
    }

    // Cloudflare's certs endpoint, answering only once the test lets it.
    private sealed class HeldCertificates : HttpMessageHandler
    {
        private readonly string _certificates;
        private int _requestCount;

        public HeldCertificates(string certificates)
        {
            _certificates = certificates;
        }

        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        public ManualResetEventSlim Fetching { get; } = new();

        public ManualResetEventSlim Answer { get; } = new();

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            Fetching.Set();
            await Task.Run(() => Answer.Wait(TimeSpan.FromSeconds(10), cancellationToken), cancellationToken);
            return new HttpResponseMessage(StatusCode) { Content = new StringContent(StatusCode == HttpStatusCode.OK ? _certificates : "", System.Text.Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Fetching.Dispose();
                Answer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // A request that has checked which team the keys are for, and is about to take them, while another team's keys
    // come in: it still gets only its own team's keys. The request is held in the clock, which it reads between the two.
    [Fact]
    public async Task Should_GiveARequestOnlyItsOwnTeamsKeys_WhileAnotherTeamsAreFetched()
    {
        const string TeamA = "a.cloudflareaccess.com";
        const string TeamB = "b.cloudflareaccess.com";
        using var tokensA = new AccessTokens(keyId: TeamA);
        using var tokensB = new AccessTokens(keyId: TeamB);
        var handler = new TeamCertificates(new Dictionary<string, string> { [TeamA] = tokensA.Certificates(), [TeamB] = tokensB.Certificates() });
        var services = new ServiceCollection();
        services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        using var clock = new HoldingClock(AccessTokens.Now);
        using var keys = new CloudflareAccessKeys(provider.GetRequiredService<IHttpClientFactory>(), _settings, clock, NullLogger<CloudflareAccessKeys>.Instance);
        var a = new CloudflareAccessSettings(TeamA, AccessTokens.Audience);
        await keys.GetAsync(a, refresh: false, Cancel);

        var held = Task.Run(() =>
        {
            HoldingClock.HoldHere.Value = true;
            return keys.GetAsync(a, refresh: false, Cancel);
        }, Cancel);
        Assert.True(clock.Held.Wait(TimeSpan.FromSeconds(10), Cancel), "The request never reached the clock.");
        await keys.GetAsync(new CloudflareAccessSettings(TeamB, AccessTokens.Audience), refresh: false, Cancel);
        clock.Release.Set();

        Assert.All(await held, k => Assert.Equal(TeamA, k.KeyId));
    }

    // A clock that holds the one request marked to be held at its first reading, until the test releases it.
    private sealed class HoldingClock : TimeProvider, IDisposable
    {
        public static readonly AsyncLocal<bool> HoldHere = new();

        private readonly DateTimeOffset _now;

        public HoldingClock(DateTimeOffset now)
        {
            _now = now;
        }

        public ManualResetEventSlim Held { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override DateTimeOffset GetUtcNow()
        {
            if (HoldHere.Value)
            {
                HoldHere.Value = false;
                Held.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }

            return _now;
        }

        public void Dispose()
        {
            Held.Dispose();
            Release.Dispose();
        }
    }

    // Each team's certs at its own address.
    private sealed class TeamCertificates : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _certificates;

        public TeamCertificates(IReadOnlyDictionary<string, string> certificates)
        {
            _certificates = certificates;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_certificates[request.RequestUri!.Host], System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // These tests never save settings: nothing reads the database.
    private sealed class NoDatabase : IDbContextFactory<ContactorDbContext>
    {
        public ContactorDbContext CreateDbContext() => throw new InvalidOperationException("No database in these tests.");
    }
}
