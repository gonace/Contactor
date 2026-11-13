using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The whole app in Production behind a fake Cloudflare Access.
/// </summary>
public sealed class CloudflareAccessTests : IDisposable
{
    private readonly AccessTokens _tokens = new();
    private readonly CertificatesHandler _handler;

    public CloudflareAccessTests()
    {
        _handler = new CertificatesHandler(_tokens.Certificates);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowThePage_WhenTokenIsValid()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/", _tokens.Create("jane@example.com"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hi Jane", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_CreateUserOnce_OnFirstSignIn()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await GetAsync(http, "/", _tokens.Create("Alex.Smith@Example.com"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var user = await db.Users.SingleAsync(Cancel);
        Assert.Equal("alex.smith@example.com", user.Email);
        Assert.Equal("Alex.smith", user.DisplayName);
    }

    [Fact]
    public async Task Should_KeepTheStoredName_WhenUserExists()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        using (var first = await GetAsync(http, "/", _tokens.Create("john@example.com")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            var john = await db.Users.SingleAsync(Cancel);
            john.DisplayName = "John E.";
            await db.SaveChangesAsync(Cancel);
        }

        using var response = await GetAsync(http, "/", _tokens.Create("john@example.com"));

        Assert.Contains("Hi John E.", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Answer403_WithoutToken()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/", token: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Access denied.", await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Should_Answer403_WhenTokenIsForAnotherApplication()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/", _tokens.Create(audience: "another-app"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer403_ForStaticFilesWithoutToken()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/app.css", token: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_IgnoreTheEmailHeader_WhenTokenIsMissing()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cf-Access-Authenticated-User-Email", "john@example.com");

        using var response = await http.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_AnswerHealthCheck_WithoutToken()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, HealthEndpoints.Path, token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("CloudflareAccess:Audience", "")]
    [InlineData("CloudflareAccess:TeamDomain", "")]
    [InlineData("CloudflareAccess:TeamDomain", "https://team.cloudflareaccess.com")]
    public async Task Should_RefuseToStart_WhenAccessIsNotConfigured(string key, string value)
    {
        var settings = new Dictionary<string, string?>(AccessTokens.Settings) { [key] = value };
        await using var app = new TestWebApp("Production", settings, setUp: false);
        app.SaveBeforeStart(new Installation { BoardType = "vm201", SetupDoneUtc = DateTime.UtcNow, AccessType = "cloudflare", SignInType = "cloudflare-access" });

        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("Cloudflare Access is the sign-in", error.Message, StringComparison.Ordinal);
    }

    // A sign-in that doesn't fit the way in, e.g. edited in the database by hand: the app doesn't start on it.
    [Theory]
    [InlineData("cloudflare", "none")]
    [InlineData("local", "cloudflare-access")]
    [InlineData(null, "none")]
    public async Task Should_RefuseToStart_OnASignInThatDoesNotFitTheWayIn(string? accessType, string signInType)
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings, setUp: false);
        app.SaveBeforeStart(new Installation { BoardType = "vm201", SetupDoneUtc = DateTime.UtcNow, AccessType = accessType, SignInType = signInType });

        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("doesn't go with its way in", error.Message, StringComparison.Ordinal);
    }

    // Only on this network, with No sign-in: everyone is let in, as one shared person.
    [Fact]
    public async Task Should_LetEveryoneIn_AsSomeone_WithNoSignIn()
    {
        await using var app = new TestWebApp("Production", setUp: false);
        app.SaveBeforeStart(new Installation { BoardType = "vm201", SetupDoneUtc = DateTime.UtcNow, AccessType = "local", SignInType = "none" });
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hi Someone", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        Assert.Equal(NoSignIn.Email, (await db.Users.SingleAsync(Cancel)).Email);
    }

    // A new installation chooses its sign-in in the setup guide: it starts without Access's settings.
    [Fact]
    public async Task Should_Start_WithoutAccessSettings_OnANewInstallation()
    {
        await using var app = new TestWebApp("Production", setUp: false);
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public void Dispose()
    {
        _tokens.Dispose();
    }

    private TestWebApp CreateApp(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>(AccessTokens.Settings);
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new TestWebApp("Production", settings, services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(AccessTokens.Now));
            services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);
        });
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Add(CloudflareAccessTokenValidator.HeaderName, token);
        }

        return await http.SendAsync(request, Cancel);
    }
}
