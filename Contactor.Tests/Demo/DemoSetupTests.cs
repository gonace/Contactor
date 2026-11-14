using System.Text.RegularExpressions;
using Contactor.Demo;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Demo;

/// <summary>The demo's settings, the copy's database, and its pages' colours.</summary>
public sealed class DemoSetupTests : IDisposable
{
    private readonly DemoFolder _folder = new();

    public void Dispose()
    {
        _folder.Dispose();
    }

    [Fact]
    public void Should_TakeTheDefaults_FromThePlan()
    {
        var options = new DemoOptions();

        Assert.Equal(10, options.MaxVisitors);
        Assert.Equal(TimeSpan.FromMinutes(30), options.IdleTimeout);
        Assert.Equal("Production", options.CopyEnvironment);
        Assert.True(new DemoOptionsValidation().Validate(null, _folder.Options()).Succeeded);
    }

    [Fact]
    public void Should_RefuseToStart_OnSettingsItCantRunWith()
    {
        var options = _folder.Options();
        options.AppPath = "";
        options.SimulatorPath = Path.Combine(_folder.Root, "missing.dll");
        options.DataRoot = Path.GetPathRoot(_folder.Root)!;
        options.MaxVisitors = 0;
        options.IdleTimeout = TimeSpan.Zero;
        options.CopyEnvironment = "Staging";

        var result = new DemoOptionsValidation().Validate(null, options);

        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Contains(failures, f => f.StartsWith("Demo:AppPath is required", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Demo:SimulatorPath: there is no", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Demo:DataRoot can't be the root", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Demo:MaxVisitors", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Demo:IdleTimeout", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Demo:CopyEnvironment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_MakeTheCopysDatabase_OnlyOnThisNetwork_WithNoSignIn()
    {
        var database = new AppCopyDatabase();
        var dataFolder = Path.Combine(_folder.DataRoot, VisitorCookie.NewId());
        Directory.CreateDirectory(dataFolder);

        await database.PrepareAsync(dataFolder, TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<ContactorDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder(database.ConnectionString(dataFolder)) { Pooling = false }.ToString()).Options;
        await using var db = new ContactorDbContext(options);
        var installation = await db.Installations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AccessCatalogue.LocalId, installation.AccessType);
        Assert.Equal(SignInCatalogue.NoneId, installation.SignInType);
        Assert.Null(installation.SetupDoneUtc);
        Assert.Empty(await db.Boards.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Should_TakeEveryColour_FromADesignToken()
    {
        // A colour is only ever a token's value, light or dark; the rules and the pages use the tokens.
        var token = new Regex(@"^\s*--[a-z-]+:\s*#[0-9A-Fa-f]{3,8};$");
        var colour = new Regex(@"#[0-9A-Fa-f]{3,8}\b");
        var css = System.Text.Encoding.UTF8.GetString(DemoPages.Stylesheet());

        var outsideTokens = css.Split('\n').Where(l => colour.IsMatch(l) && !token.IsMatch(l)).ToList();
        var pages = string.Concat(DemoPages.Page(TimeSpan.FromMinutes(30)), DemoPages.Busy(), DemoPages.Stopped(TimeSpan.FromMinutes(30)), DemoPages.NotStarted());

        Assert.Empty(outsideTokens);
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(colour, pages);
        Assert.DoesNotContain("style=", pages, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_UseTheAppsTokens_WithTheAppsValues()
    {
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web", "wwwroot", "app.css"));
        var css = System.Text.Encoding.UTF8.GetString(DemoPages.Stylesheet());
        var token = new Regex(@"(--[a-z-]+):\s*(#[0-9A-Fa-f]{3,8});");

        // The light ones come first in both files, then the dark ones.
        var appTokens = token.Matches(app).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        var demoTokens = token.Matches(css).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

        Assert.NotEmpty(demoTokens);
        Assert.All(demoTokens, t => Assert.Contains(t, appTokens));
    }
}
