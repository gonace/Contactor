using Contactor.Web.Features.Access;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Contactor.Tests.Web;

/// <summary>The catalogues of ways in and sign-in methods, and which ones an installation uses.</summary>
public sealed class MethodCatalogueTests
{
    private static readonly Dictionary<string, string?> AccessSettings = new()
    {
        ["CloudflareAccess:TeamDomain"] = "myteam.cloudflareaccess.com",
        ["CloudflareAccess:Audience"] = "aud",
    };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_ListEachMethodOnce_InLowerCase()
    {
        var access = AccessCatalogue.All.Select(m => m.Id).ToList();
        var signIns = SignInCatalogue.All.Select(m => m.Id).ToList();

        Assert.Equal(access.Distinct().Count(), access.Count);
        Assert.Equal(signIns.Distinct().Count(), signIns.Count);
        Assert.All(access.Concat(signIns), id => Assert.Equal(id.ToLowerInvariant(), id));
    }

    // The guide offers what a way in works with: each must be a sign-in the app has.
    [Fact]
    public void Should_OfferOnlyKnownSignIns_ForEachWayIn()
    {
        Assert.All(AccessCatalogue.All, m =>
        {
            Assert.NotEmpty(m.SignIns);
            Assert.All(m.SignIns, id => Assert.NotNull(SignInCatalogue.Find(id)));
        });
        Assert.Contains(CloudflareMethods.AccessId, AccessCatalogue.Find(CloudflareMethods.TunnelId)!.SignIns);
    }

    // A new installation, or one whose sign-in was reset: nothing runs, and the app is in setup mode.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ChooseNothing_WhenNothingIsSaved(bool hasInstallation)
    {
        await using var database = await CreateAsync(hasInstallation ? new Installation { BoardType = "vm201" } : null);
        var (access, signIn) = await ApplyAsync(database);

        Assert.Null(access.Current);
        Assert.Null(signIn.Current);
    }

    [Fact]
    public async Task Should_UseTheSavedMethods_IgnoringCase()
    {
        await using var database = await CreateAsync(new Installation { AccessType = " Cloudflare ", SignInType = "CLOUDFLARE-ACCESS" });
        var (access, signIn) = await ApplyAsync(database);

        Assert.Equal(CloudflareMethods.TunnelId, access.Current?.Id);
        Assert.Equal(CloudflareMethods.AccessId, signIn.Current?.Id);
        Assert.IsType<CloudflareTunnel>(access.Current!.Create(database.Services));
        Assert.IsType<CloudflareAccess>(signIn.Current!.Create(database.Services));
    }

    [Fact]
    public async Task Should_RefuseToStart_OnASavedWayInItDoesNotKnow()
    {
        await using var database = await CreateAsync(new Installation { AccessType = "carrier-pigeon" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(Cancel));
        Assert.Contains("carrier-pigeon", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_RefuseToStart_OnASavedSignInItDoesNotKnow()
    {
        await using var database = await CreateAsync(new Installation { SignInType = "secret-handshake" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Services.GetRequiredService<SignInChoice>().ApplySavedAsync(Cancel));
        Assert.Contains("secret-handshake", error.Message, StringComparison.Ordinal);
    }

    private static async Task<TestDatabase> CreateAsync(Installation? installation)
    {
        var database = TestDatabase.Create(
            (services, configuration) =>
            {
                services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
                services.AddBoards(configuration);
                services.AddCloudflare(configuration);
                services.AddTailscale(configuration);
                services.AddAccess(configuration);
                services.AddSignIn(configuration);
            },
            AccessSettings);
        await database.Services.InitializeDatabaseAsync(Cancel);
        if (installation is not null)
        {
            await using var db = await database.Factory.CreateDbContextAsync(Cancel);
            db.Installations.Add(installation);
            await db.SaveChangesAsync(Cancel);
        }

        return database;
    }

    private static async Task<(AccessChoice Access, SignInChoice SignIn)> ApplyAsync(TestDatabase database)
    {
        var access = database.Services.GetRequiredService<AccessChoice>();
        var signIn = database.Services.GetRequiredService<SignInChoice>();
        await access.ApplySavedAsync(Cancel);
        await signIn.ApplySavedAsync(Cancel);
        return (access, signIn);
    }
}
