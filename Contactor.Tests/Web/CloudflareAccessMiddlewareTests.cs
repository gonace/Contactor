using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The whole app in Production behind a fake Cloudflare Access.
/// </summary>
public sealed class CloudflareAccessMiddlewareTests : IDisposable
{
    private readonly AccessTokens _tokens = new();
    private readonly CertificatesHandler _handler;

    public CloudflareAccessMiddlewareTests()
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
        Assert.Contains("Hej Jane", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
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

        Assert.Contains("Hej John E.", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Answer403_WithoutToken()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/", token: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Åtkomst nekad.", await response.Content.ReadAsStringAsync(Cancel));
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

    [Fact]
    public async Task Should_RefuseToStart_WhenDevUserIsSetInProduction()
    {
        await using var app = CreateApp(new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });

        var error = Assert.Throws<OptionsValidationException>(() => app.CreateClient());

        Assert.Contains("DevUserEmail", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CloudflareAccess:Audience", "")]
    [InlineData("CloudflareAccess:TeamDomain", "")]
    [InlineData("CloudflareAccess:TeamDomain", "https://team.example.com")]
    public async Task Should_RefuseToStart_WhenAccessIsNotConfigured(string key, string value)
    {
        await using var app = CreateApp(new Dictionary<string, string?> { [key] = value });

        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task Should_SignInAsDevUser_InDevelopment()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        using var response = await GetAsync(http, "/", token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hej John", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
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
