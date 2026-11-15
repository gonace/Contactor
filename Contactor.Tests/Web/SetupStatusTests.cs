using Contactor.Web.Features.Setup;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;

namespace Contactor.Tests.Web;

/// <summary><c>setup-status</c>, which tells the install script how far the setup is.</summary>
public sealed class SetupStatusTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_SayThereIsNoDatabase_BeforeTheFirstStart()
    {
        await using var database = TestDatabase.Create();

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((1, ""), (result.ExitCode, result.Output));
        Assert.StartsWith("setup-status: there is no database at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SaySetupCode_ForANewInstallation()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((0, "setup-code\n", ""), (result.ExitCode, result.Output, result.Error));
    }

    // reset-sign-in forgets the sign-in of an installation that was set up: the setup code is needed again.
    [Fact]
    public async Task Should_SaySetupCode_OnceTheSignInIsForgotten()
    {
        await using var database = await WithAsync(new Installation { AccessType = "local", SetupDoneUtc = DateTime.UtcNow });

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((0, "setup-code\n"), (result.ExitCode, result.Output));
    }

    [Fact]
    public async Task Should_SaySetupGuide_WhileTheGuideIsNotFinished()
    {
        await using var database = await WithAsync(new Installation { AccessType = "local", SignInType = "none" });

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((0, "setup-guide\n"), (result.ExitCode, result.Output));
    }

    [Fact]
    public async Task Should_SayDone_WithTheTunnelsAddress()
    {
        await using var database = await WithAsync(
            new Installation { AccessType = "cloudflare", SignInType = "cloudflare-access", SetupDoneUtc = DateTime.UtcNow },
            new CloudflareSetting { TunnelToken = "secret-token", Address = "relays.example.com" });

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((0, "done\nhttps://relays.example.com\n", ""), (result.ExitCode, result.Output, result.Error));
        Assert.DoesNotContain("secret-token", result.Output, StringComparison.Ordinal);
    }

    // A tunnel set up from its token alone has no saved address: the script shows the machine's own.
    [Fact]
    public async Task Should_SayNoAddress_ForATunnelWithoutASavedAddress()
    {
        await using var database = await WithAsync(
            new Installation { AccessType = "cloudflare", SignInType = "cloudflare-access", SetupDoneUtc = DateTime.UtcNow },
            new CloudflareSetting { TunnelToken = "secret-token" });

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((0, "done\n", ""), (result.ExitCode, result.Output, result.Error));
    }

    // A database file the app hasn't brought up to date yet, e.g. while it is starting for the first time.
    [Fact]
    public async Task Should_SayTheDatabaseCantBeRead_BeforeItsMigrations()
    {
        await using var database = TestDatabase.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Cancel);
        }

        var result = Status(database, new FakeTailscale(null));

        Assert.Equal((1, ""), (result.ExitCode, result.Output));
        Assert.StartsWith("setup-status: the database can't be read yet (", result.Error, StringComparison.Ordinal);
    }

    // Only on this network: the address is the machine's own, which the script knows.
    [Fact]
    public async Task Should_SayNoAddress_OnlyOnThisNetwork()
    {
        await using var database = await WithAsync(new Installation { AccessType = "local", SignInType = "none", SetupDoneUtc = DateTime.UtcNow });

        var result = Status(database, new FakeTailscale("relays.tail1234.ts.net."));

        Assert.Equal((0, "done\n"), (result.ExitCode, result.Output));
    }

    [Fact]
    public async Task Should_SayTheTailscaleAddress_WhenTailscaleKnowsIt()
    {
        var installation = new Installation { AccessType = "tailscale", SignInType = "tailscale-identity", SetupDoneUtc = DateTime.UtcNow };
        await using var database = await WithAsync(installation);

        Assert.Equal("done\nhttps://relays.tail1234.ts.net\n", Status(database, new FakeTailscale("relays.tail1234.ts.net.")).Output);
        Assert.Equal("done\n", Status(database, new FakeTailscale(null)).Output);
    }

    private static async Task<TestDatabase> WithAsync(Installation installation, CloudflareSetting? cloudflare = null)
    {
        var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Installations.Add(installation);
        if (cloudflare is not null)
        {
            db.CloudflareSettings.Add(cloudflare);
        }

        await db.SaveChangesAsync(Cancel);
        return database;
    }

    // The command as it runs on the machine, keeping what it writes rather than printing it in the test run.
    private static (int ExitCode, string Output, string Error) Status(TestDatabase database, ITailscaleProcess tailscale)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        var exitCode = SetupStatus.Run([SetupStatus.Command, $"--ConnectionStrings:Default={database.ConnectionString}"], tailscale, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    // tailscale status --json, with the app's name on the Tailscale network, or failing as a tailscaled that isn't
    // running does.
    private sealed class FakeTailscale : ITailscaleProcess
    {
        private readonly string? _dnsName;

        public FakeTailscale(string? dnsName)
        {
            _dnsName = dnsName;
        }

        public Task<int> RunDaemonAsync(Action<string> output, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(_dnsName is null
                ? new TailscaleCommandResult(1, "failed to connect to local tailscaled")
                : new TailscaleCommandResult(0, "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"" + _dnsName + "\"}}"));
        }

        public Task<TailscaleCommandResult> LogInAsync(string authKey, string hostName, bool force, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(new TailscaleCommandResult(0, ""));
        }
    }
}
