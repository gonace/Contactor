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
        var option = Assert.Single(handler.SignIns());
        Assert.Equal((CloudflareMethods.AccessId, true), (option.Method.Id, option.Recommended));

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
