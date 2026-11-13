using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Contactor.Tests.Web;

/// <summary>Cloudflare's settings as the setup guide saves them, and the token check that follows them.</summary>
public sealed class CloudflareSettingsTests : IDisposable
{
    private readonly AccessTokens _tokens = new();
    private readonly CertificatesHandler _handler;

    public CloudflareSettingsTests()
    {
        _handler = new CertificatesHandler(_tokens.Certificates);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_UseTheAppsSettings_UntilTheGuideSavesItsOwn()
    {
        await using var database = await CreateAsync(new Dictionary<string, string?>(AccessTokens.Settings) { ["Tunnel:Token"] = " from-env " });
        var settings = database.Services.GetRequiredService<CloudflareSettings>();

        Assert.Equal((AccessTokens.TeamDomain, AccessTokens.Audience), (settings.Access.TeamDomain, settings.Access.Audience));
        Assert.Equal("from-env", settings.Tunnel.Token);
        Assert.Null(settings.Tunnel.Address);

        Assert.Null(await settings.SaveAccessAsync(" https://Other.CloudflareAccess.com/ ", " aud-new ", Cancel));
        Assert.Null(await settings.SaveTunnelAsync("saved-token", "relays.example.com", Cancel));

        // What was saved wins, also after a restart.
        var restarted = new CloudflareSettings(
            database.Factory,
            database.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TunnelOptions>>(),
            database.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudflareAccessOptions>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudflareSettings>.Instance);
        await restarted.LoadAsync(Cancel);
        Assert.Equal(("other.cloudflareaccess.com", "aud-new"), (restarted.Access.TeamDomain, restarted.Access.Audience));
        Assert.Equal(("saved-token", "relays.example.com"), (restarted.Tunnel.Token, restarted.Tunnel.Address));
        restarted.Dispose();
    }

    [Theory]
    [InlineData("", "relays.example.com", "tunnel token")]
    [InlineData("two words", "relays.example.com", "tunnel token")]
    [InlineData("token", "", "address")]
    [InlineData("token", "relays", "address")]
    [InlineData("token", "relays example.com", "address")]
    public async Task Should_SaveNoTunnel_ThatCantBeUsed(string token, string address, string named)
    {
        await using var database = await CreateAsync(AccessTokens.Settings);
        var settings = database.Services.GetRequiredService<CloudflareSettings>();

        var problem = await settings.SaveTunnelAsync(token, address, Cancel);

        Assert.Contains(named, problem, StringComparison.Ordinal);
        Assert.False(await HasRowAsync(database));
    }

    [Fact]
    public async Task Should_SaveNoTunnelToken_ThatIsHuge()
    {
        await using var database = await CreateAsync(AccessTokens.Settings);
        var settings = database.Services.GetRequiredService<CloudflareSettings>();

        Assert.NotNull(await settings.SaveTunnelAsync(new string('a', CloudflareSetting.TunnelTokenMaxLength + 1), "relays.example.com", Cancel));
        Assert.False(await HasRowAsync(database));
    }

    [Theory]
    [InlineData("", "aud", "team domain")]
    [InlineData("not a host", "aud", "team domain")]
    [InlineData("team.example.com", "", "AUD tag")]
    [InlineData("team.example.com", "two parts", "AUD tag")]
    public async Task Should_SaveNoAccess_ThatCantBeUsed(string teamDomain, string audience, string named)
    {
        await using var database = await CreateAsync(AccessTokens.Settings);
        var settings = database.Services.GetRequiredService<CloudflareSettings>();

        var problem = await settings.SaveAccessAsync(teamDomain, audience, Cancel);

        Assert.Contains(named, problem, StringComparison.Ordinal);
        Assert.False(await HasRowAsync(database));
        Assert.Equal(AccessTokens.TeamDomain, settings.Access.TeamDomain);
    }

    // A new team: tokens from the team before are refused, and the keys come from the new team.
    [Fact]
    public async Task Should_CheckTokens_AgainstTheTeamSavedNow()
    {
        await using var database = await CreateAsync(AccessTokens.Settings);
        var validator = database.Services.GetRequiredService<CloudflareAccessTokenValidator>();
        var token = _tokens.Create("jane@example.com");
        Assert.Equal("jane@example.com", await validator.ValidateAsync(token, Cancel));

        await database.Services.GetRequiredService<CloudflareSettings>().SaveAccessAsync("other.cloudflareaccess.com", AccessTokens.Audience, Cancel);

        Assert.Null(await validator.ValidateAsync(token, Cancel));
        Assert.Equal(new Uri("https://other.cloudflareaccess.com/cdn-cgi/access/certs"), _handler.LastRequestUri);
    }

    public void Dispose()
    {
        _tokens.Dispose();
    }

    private async Task<TestDatabase> CreateAsync(IReadOnlyDictionary<string, string?> settings)
    {
        var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(AccessTokens.Now));
                services.AddCloudflare(configuration);
                services.AddHttpClient(CloudflareAccessKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);
            },
            new Dictionary<string, string?>(settings));
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        return database;
    }

    private static async Task<bool> HasRowAsync(TestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.CloudflareSettings.AnyAsync(Cancel);
    }
}
