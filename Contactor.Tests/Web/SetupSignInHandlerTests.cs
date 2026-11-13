using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Contactor.Tests.Web;

/// <summary>Part 1 of the setup guide: how people reach the app, and how they sign in.</summary>
public sealed class SetupSignInHandlerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_GoThroughPart1_WithCloudflare()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        Assert.Equal(SetupPart1Step.WayIn, handler.Step);
        Assert.Empty(handler.SignIns());

        Assert.Null(await handler.ChooseCloudflareAsync("tunnel-token", "https://Relays.Example.com/", Cancel));
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
        Assert.Equal("relays.example.com", handler.Tunnel.Address);
        var options = handler.SignIns();
        Assert.Equal((CloudflareMethods.AccessId, true, true), (options[0].Method.Id, options[0].Allowed, options[0].Recommended));
        var none = Assert.Single(options, o => o.Method.Id == SignInCatalogue.NoneId);
        Assert.Equal((false, "Only on this network."), (none.Allowed, none.Reason));

        Assert.Null(await handler.ChooseCloudflareAccessAsync("myteam.cloudflareaccess.com", "aud-123", Cancel));
        Assert.Equal(SetupPart1Step.Done, handler.Step);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var installation = await db.Installations.SingleAsync(Cancel);
        Assert.Equal((CloudflareMethods.TunnelId, CloudflareMethods.AccessId), (installation.AccessType, installation.SignInType));

        // Set up: neither can be chosen again here (reset-sign-in is the way back).
        Assert.NotNull(await handler.ChooseCloudflareAsync("other", "other.example.com", Cancel));
        Assert.NotNull(await handler.ChooseCloudflareAccessAsync("other.cloudflareaccess.com", "aud", Cancel));
    }

    [Fact]
    public async Task Should_GoThroughPart1_OnlyOnThisNetwork_WithNoSignIn()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();

        Assert.Null(await handler.ChooseWayInAsync(AccessCatalogue.LocalId, token: null, address: null, Cancel));
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
        var options = handler.SignIns();
        Assert.Equal((SignInCatalogue.NoneId, true, false), (options[0].Method.Id, options[0].Allowed, options[0].Recommended));
        Assert.Equal("Only with Cloudflare.", Assert.Single(options, o => o.Method.Id == CloudflareMethods.AccessId).Reason);

        // Cloudflare Access can't go with it; No sign-in can.
        Assert.NotNull(await handler.ChooseSignInAsync(CloudflareMethods.AccessId, "myteam.cloudflareaccess.com", "aud", Cancel));
        Assert.Null(await handler.ChooseSignInAsync(SignInCatalogue.NoneId, null, null, Cancel));
        Assert.Equal(SetupPart1Step.Done, handler.Step);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var installation = await db.Installations.SingleAsync(Cancel);
        Assert.Equal((AccessCatalogue.LocalId, SignInCatalogue.NoneId), (installation.AccessType, installation.SignInType));
    }

    // Never a public link without a sign-in.
    [Fact]
    public async Task Should_RefuseNoSignIn_WithCloudflare()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        Assert.NotNull(await handler.ChooseNoSignInAsync(Cancel));

        await handler.ChooseWayInAsync(CloudflareMethods.TunnelId, "tunnel-token", "relays.example.com", Cancel);

        Assert.NotNull(await handler.ChooseSignInAsync(SignInCatalogue.NoneId, null, null, Cancel));
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
    }

    [Fact]
    public async Task Should_ChooseNothing_WithSettingsThatCantBeUsed()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();

        Assert.NotNull(await handler.ChooseCloudflareAsync("", "relays.example.com", Cancel));
        Assert.Equal(SetupPart1Step.WayIn, handler.Step);

        // No way in yet: Cloudflare Access can't be the sign-in.
        Assert.NotNull(await handler.ChooseCloudflareAccessAsync("myteam.cloudflareaccess.com", "aud", Cancel));
        Assert.Equal(SetupPart1Step.WayIn, handler.Step);

        await handler.ChooseCloudflareAsync("tunnel-token", "relays.example.com", Cancel);
        Assert.NotNull(await handler.ChooseCloudflareAccessAsync("evil.example.com", "aud", Cancel));
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
    }

    // Back on the way-in step, the token field is empty: the saved token stays, and a new address is taken.
    [Fact]
    public async Task Should_KeepTheSavedToken_WhenGoingBack()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        await handler.ChooseCloudflareAsync("tunnel-token", "relays.example.com", Cancel);

        Assert.Null(await handler.ChooseCloudflareAsync("", "contactor.example.com", Cancel));

        var settings = database.Services.GetRequiredService<CloudflareSettings>();
        Assert.Equal(("tunnel-token", "contactor.example.com"), (settings.Tunnel.Token, settings.Tunnel.Address));
    }

    // After reset-sign-in: with the address saved, the guide asks only for the sign-in. Set up with .env there is
    // none, and the guide sends people to it, so it asks for the way in again.
    [Fact]
    public async Task Should_AskForTheAddress_WhenItIsMissing_AfterAReset()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        await database.Services.GetRequiredService<AccessChoice>().ChooseAsync(AccessCatalogue.Find(CloudflareMethods.TunnelId)!, Cancel);

        Assert.Equal(SetupPart1Step.WayIn, handler.Step);
        Assert.NotNull(await handler.ChooseCloudflareAccessAsync("myteam.cloudflareaccess.com", "aud", Cancel));

        await database.Services.GetRequiredService<CloudflareSettings>().SaveTunnelAsync("tunnel-token", "relays.example.com", Cancel);
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
        Assert.Null(await handler.ChooseCloudflareAccessAsync("myteam.cloudflareaccess.com", "aud", Cancel));
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
            services.AddCloudflare(configuration);
            services.AddAccess(configuration);
            services.AddSignIn(configuration);
            services.AddSingleton<SetupSignInHandler>();
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await database.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(Cancel);
        await database.Services.GetRequiredService<SignInChoice>().ApplySavedAsync(Cancel);
        return database;
    }
}
