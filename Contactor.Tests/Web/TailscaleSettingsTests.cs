using Contactor.Web.Features.Tailscale;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>Tailscale's settings as the setup guide saves them, and the auth key's file.</summary>
public sealed class TailscaleSettingsTests
{
    private const string AuthKey = "tskey-auth-kExample-SECRET1234";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_StartWithNoKey_AndTheDefaultName()
    {
        await using var database = await CreateAsync();
        var settings = database.Services.GetRequiredService<TailscaleSettings>();

        await settings.LoadAsync(Cancel);

        Assert.Equal(new TailscaleNetworkSettings("", "contactor"), settings.Current);
    }

    [Fact]
    public async Task Should_SaveTheKeyAndName_AndReadThemBackAtStart()
    {
        await using var database = await CreateAsync();
        var settings = database.Services.GetRequiredService<TailscaleSettings>();
        var changes = 0;
        settings.Changed += (_, _) => changes++;

        Assert.Null(await settings.SaveAsync($"  {AuthKey} ", " Workshop-1 ", Cancel));
        Assert.Null(await settings.SaveAsync(AuthKey, "workshop-1", Cancel));

        Assert.Equal(1, changes);
        var restarted = new TailscaleSettings(database.Factory, database.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TailscaleSettings>>());
        await restarted.LoadAsync(Cancel);
        Assert.Equal(new TailscaleNetworkSettings(AuthKey, "workshop-1"), restarted.Current);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(TailscaleSetting.SingletonId, (await db.TailscaleSettings.SingleAsync(Cancel)).Id);
    }

    [Theory]
    [InlineData("", "contactor", "auth key")]
    [InlineData("tskey-", "contactor", "auth key")]
    [InlineData("abc-123", "contactor", "auth key")]
    [InlineData("tskey-auth-k1 2", "contactor", "auth key")]
    [InlineData(AuthKey, "", "name")]
    [InlineData(AuthKey, "-contactor", "name")]
    [InlineData(AuthKey, "contactor-", "name")]
    [InlineData(AuthKey, "con.tactor", "name")]
    [InlineData(AuthKey, "contäctor", "name")]
    [InlineData(AuthKey, "Contactor", "name")]
    public void Should_RefuseWhatTailscaleWouldNot_InPlainWords(string authKey, string hostName, string about)
    {
        Assert.Contains(about, TailscaleSettings.Check(authKey, hostName), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseAKeyOrNameThatIsTooLong()
    {
        Assert.NotNull(TailscaleSettings.Check("tskey-" + new string('a', TailscaleSetting.AuthKeyMaxLength), "contactor"));
        Assert.NotNull(TailscaleSettings.Check(AuthKey, new string('a', TailscaleSetting.HostNameMaxLength + 1)));
        Assert.Null(TailscaleSettings.Check(AuthKey, new string('a', TailscaleSetting.HostNameMaxLength)));
    }

    [Fact]
    public async Task Should_SaveNothing_WhenSomethingIsWrong()
    {
        await using var database = await CreateAsync();
        var settings = database.Services.GetRequiredService<TailscaleSettings>();

        Assert.NotNull(await settings.SaveAsync("not-a-key", "contactor", Cancel));

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.TailscaleSettings.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_GiveTheKeyInAFileOnlyTheAppCanRead_AndDeleteItAfter()
    {
        var folder = Path.Combine(Path.GetTempPath(), "contactor-tailscale-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? seen = null;
            var result = await TailscaleProcess.WithKeyFileAsync(folder, AuthKey, async path =>
            {
                seen = path;
                Assert.Equal(AuthKey, await File.ReadAllTextAsync(path, Cancel));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                }

                return 7;
            });

            Assert.Equal(7, result);
            Assert.NotNull(seen);
            Assert.False(File.Exists(seen));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TailscaleProcess.WithKeyFileAsync<int>(folder, AuthKey, _ => throw new InvalidOperationException("tailscale failed")));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Should_UseTheImagesPaths_ByDefault()
    {
        var options = new TailscaleOptions();

        Assert.Equal(("/usr/local/bin/tailscaled", "/usr/local/bin/tailscale", "/data/tailscale", "http://127.0.0.1:8080"),
            (options.DaemonPath, options.CliPath, options.StateDirectory, options.Target));
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = TestDatabase.Create((services, configuration) => services.AddTailscale(configuration));
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        _ = database.Services.GetRequiredService<IOptions<TailscaleOptions>>();
        return database;
    }
}
