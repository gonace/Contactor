using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Setup;
using Contactor.Web.Features.Tailscale;
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

        Assert.Null(await handler.ChooseWayInAsync(AccessCatalogue.LocalId, new WayInFields(), Cancel));
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

        Assert.Null(await handler.ChooseWayInAsync(CloudflareMethods.TunnelId, new WayInFields("tunnel-token", "relays.example.com"), Cancel));
        Assert.Equal(CloudflareMethods.TunnelId, handler.WayIn?.Id);

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

    // Two tabs of the guide at once: one chooses Cloudflare as the way in while the other chooses No sign-in. However
    // they meet, the installation never ends up with a public link and No sign-in.
    [Fact]
    public async Task Should_NeverCombineCloudflare_WithNoSignIn_FromTwoTabs()
    {
        for (var round = 0; round < 20; round++)
        {
            await using var database = await CreateAsync();
            var handler = database.Services.GetRequiredService<SetupSignInHandler>();
            await handler.ChooseLocalAsync(Cancel);

            await Task.WhenAll(
                Task.Run(() => handler.ChooseCloudflareAsync("tunnel-token", "relays.example.com", Cancel), Cancel),
                Task.Run(() => handler.ChooseNoSignInAsync(Cancel), Cancel));

            await using var db = await database.Factory.CreateDbContextAsync(Cancel);
            var installation = await db.Installations.SingleAsync(Cancel);
            Assert.False(
                installation.AccessType == CloudflareMethods.TunnelId && installation.SignInType == SignInCatalogue.NoneId,
                "Cloudflare and No sign-in were both saved.");
            Assert.Equal(installation.AccessType, handler.WayIn?.Id);
        }
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

    [Fact]
    public async Task Should_GoThroughPart1_WithTailscale()
    {
        await using var database = await CreateAsync(services =>
        {
            services.AddSingleton<ITailscaleProcess>(new OnTheNetwork());
            services.Configure<TailscaleOptions>(o => o.ServeAddress = "127.0.0.2:8081");
        });
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();

        Assert.Null(await handler.ChooseWayInAsync(TailscaleMethods.NetworkId, new WayInFields(AuthKey: "tskey-auth-kExample-1", HostName: "Relays"), Cancel));
        Assert.Equal(SetupPart1Step.SignIn, handler.Step);
        Assert.Equal(new TailscaleNetworkSettings("tskey-auth-kExample-1", "relays"), database.Services.GetRequiredService<TailscaleSettings>().Current);
        var options = handler.SignIns();
        Assert.Equal((TailscaleMethods.IdentityId, true, true), (options[0].Method.Id, options[0].Allowed, options[0].Recommended));
        Assert.Equal("Only with Cloudflare.", Assert.Single(options, o => o.Method.Id == CloudflareMethods.AccessId).Reason);

        // Never without a sign-in, and not before the app is on the Tailscale network: the guide sends people there.
        Assert.NotNull(await handler.ChooseNoSignInAsync(Cancel));
        Assert.Equal("Wait until Contactor is on your Tailscale network: people open it there.", await handler.ChooseTailscaleIdentityAsync(Cancel));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var network = database.Services.GetRequiredService<TailscaleNetwork>();
        var running = network.RunAsync(stop.Token);
        await Wait.UntilAsync(() => network.Status.Online);
        Assert.Null(await handler.ChooseSignInAsync(TailscaleMethods.IdentityId, null, null, Cancel));
        await stop.CancelAsync();
        await running;

        Assert.Equal(SetupPart1Step.Done, handler.Step);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var installation = await db.Installations.SingleAsync(Cancel);
        Assert.Equal((TailscaleMethods.NetworkId, TailscaleMethods.IdentityId), (installation.AccessType, installation.SignInType));
    }

    [Fact]
    public async Task Should_RefuseTailscaleIdentity_WithAnotherWayIn()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        await handler.ChooseCloudflareAsync("tunnel-token", "relays.example.com", Cancel);

        Assert.Equal("Tailscale identity only works when people reach the app through Tailscale.", await handler.ChooseTailscaleIdentityAsync(Cancel));
        Assert.Equal("Only with Tailscale.", Assert.Single(handler.SignIns(), o => o.Method.Id == TailscaleMethods.IdentityId).Reason);
    }

    // Back on the way-in step, the auth key field is empty: the saved key stays, and a new name is taken.
    [Fact]
    public async Task Should_KeepTheSavedAuthKey_WhenGoingBack()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();
        Assert.Null(await handler.ChooseTailscaleAsync("tskey-auth-kExample-1", "relays", Cancel));

        Assert.Null(await handler.ChooseTailscaleAsync("", "workshop", Cancel));

        Assert.Equal(new TailscaleNetworkSettings("tskey-auth-kExample-1", "workshop"), database.Services.GetRequiredService<TailscaleSettings>().Current);
    }

    // A first choice of Tailscale needs an auth key.
    [Fact]
    public async Task Should_AskForAnAuthKey_TheFirstTime()
    {
        await using var database = await CreateAsync();
        var handler = database.Services.GetRequiredService<SetupSignInHandler>();

        Assert.NotNull(await handler.ChooseTailscaleAsync("", "relays", Cancel));
        Assert.Equal(SetupPart1Step.WayIn, handler.Step);
    }

    private static async Task<TestDatabase> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
            services.AddCloudflare(configuration);
            services.AddTailscale(configuration);
            services.AddAccess(configuration);
            services.AddSignIn(configuration);
            services.AddSingleton<SetupSignInHandler>();
            configure?.Invoke(services);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await database.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(Cancel);
        await database.Services.GetRequiredService<SignInChoice>().ApplySavedAsync(Cancel);
        return database;
    }

    // tailscaled with the app on the Tailscale network already, and Tailscale Serve working.
    private sealed class OnTheNetwork : ITailscaleProcess
    {
        public async Task<int> RunDaemonAsync(Action<string> output, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(new TailscaleCommandResult(0, arguments switch
            {
                ["status", ..] => "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"relays.tail1234.ts.net.\"}}",
                ["serve", "status", ..] => "{\"Web\":{\"relays.tail1234.ts.net:443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.2:8081\"}}}}}",
                _ => "",
            }));
        }

        public Task<TailscaleCommandResult> LogInAsync(string authKey, string hostName, bool force, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(new TailscaleCommandResult(0, ""));
        }
    }
}
