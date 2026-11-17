using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Features.Updates;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Pages and parts of pages closed while their first load is under way: when the load ends, they must not subscribe,
/// or their handlers stay for the life of the app, holding the closed page and running on every event.
/// </summary>
public sealed class PagesClosedWhileLoadingTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenTheBoardFormClosesWhileItLoads()
    {
        await using var app = new TestWebApp(configureServices: TestWebApp.WithLostBoard);
        var registry = app.Services.GetRequiredService<BoardRegistry>();

        await CloseWhileSigningInAsync<BoardForm>(app.Services, new Dictionary<string, object?> { [nameof(BoardForm.BoardId)] = registry.All[0].BoardId });

        Assert.Equal(0, PageRenderer.Handlers<BoardForm>(registry, nameof(BoardRegistry.StateChanged)));
    }

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenAddABoardClosesWhileItLoads()
    {
        await using var app = new TestWebApp();
        var registry = app.Services.GetRequiredService<BoardRegistry>();

        await CloseWhileSigningInAsync<BoardNew>(app.Services);

        Assert.Equal(0, PageRenderer.Handlers<BoardNew>(registry, nameof(BoardRegistry.BoardsChanged)));
    }

    // Events doesn't wait for the sign-in: its load waits for the database instead.
    [Fact]
    public async Task Should_LeaveNoHandlers_WhenEventsClosesWhileItLoads()
    {
        var queries = new HeldQueries();
        await using var app = new TestWebApp(configureServices: queries.AddTo);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var changes = app.Services.GetRequiredService<RelayChanges>();
        queries.Hold(sql => sql.Contains("FROM \"EventLog\"", StringComparison.Ordinal));

        var (page, rendered) = PageRenderer.StartHeld<Events>(app.Services, Task.FromResult(PageRenderer.SignedIn(John, "John")));
        page.Close();
        queries.Release();
        await rendered.WaitAsync(Cancel);
        Assert.NotEqual(0, queries.Holds);

        Assert.Equal(0, PageRenderer.Handlers<Events>(changes, nameof(RelayChanges.Changed)));
        Assert.Equal(0, PageRenderer.Handlers<Events>(registry, nameof(BoardRegistry.StateChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Events>(registry, nameof(BoardRegistry.BoardsChanged)));
    }

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenSettingsClosesWhileItLoads()
    {
        await using var app = new TestWebApp();
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var changes = app.Services.GetRequiredService<RelayChanges>();

        await CloseWhileSigningInAsync<Settings>(app.Services);

        Assert.Equal(0, PageRenderer.Handlers<Settings>(registry, nameof(BoardRegistry.StateChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Settings>(registry, nameof(BoardRegistry.BoardsChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Settings>(changes, nameof(RelayChanges.Changed)));
    }

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenTheSetupGuideClosesWhileItSignsIn()
    {
        await using var app = SetupApp(boardType: null);
        await SignInOnceAsync(app);

        await CloseWhileSigningInAsync<SetupGuide>(app.Services);

        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(app.Services.GetRequiredService<CloudflareTunnel>(), nameof(CloudflareTunnel.ConnectedChanged)));
        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(app.Services.GetRequiredService<TailscaleNetwork>(), nameof(TailscaleNetwork.StatusChanged)));
        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(app.Services.GetRequiredService<BoardRegistry>(), nameof(BoardRegistry.StateChanged)));
    }

    // The guide's board loads after it subscribed to the tunnel and Tailscale: closed then, it lets go of those and
    // doesn't subscribe to the board.
    [Fact]
    public async Task Should_LeaveNoHandlers_WhenTheSetupGuideClosesWhileItsBoardLoads()
    {
        var queries = new HeldQueries();
        await using var app = SetupApp(boardType: "vm201", queries.AddTo);
        await SignInOnceAsync(app);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        Assert.NotEmpty(registry.All);
        queries.Hold(sql => sql.Contains("FROM \"Relay\" ", StringComparison.Ordinal));

        var (page, rendered) = PageRenderer.StartHeld<SetupGuide>(app.Services, Task.FromResult(PageRenderer.SignedIn(John, "John")));
        page.Close();
        queries.Release();
        await rendered.WaitAsync(Cancel);
        Assert.NotEqual(0, queries.Holds);

        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(app.Services.GetRequiredService<CloudflareTunnel>(), nameof(CloudflareTunnel.ConnectedChanged)));
        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(app.Services.GetRequiredService<TailscaleNetwork>(), nameof(TailscaleNetwork.StatusChanged)));
        Assert.Equal(0, PageRenderer.Handlers<SetupGuide>(registry, nameof(BoardRegistry.StateChanged)));
    }

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenTheTabBarClosesWhileItLoads()
    {
        await using var app = new TestWebApp();
        var registry = app.Services.GetRequiredService<BoardRegistry>();

        await CloseWhileSigningInAsync<TabBar>(app.Services);

        Assert.Equal(0, PageRenderer.Handlers<TabBar>(app.Services.GetRequiredService<BoardTrafficSetting>(), nameof(BoardTrafficSetting.Changed)));
        // The toasts' watch on every board's trace starts by listening for boards added.
        Assert.Equal(0, PageRenderer.Handlers<BoardTraceWatch>(registry, nameof(BoardRegistry.BoardsChanged)));
    }

    [Fact]
    public async Task Should_LeaveNoHandlers_WhenTheUpdateBoxClosesWhileItLoads()
    {
        await using var app = new TestWebApp();

        await CloseWhileSigningInAsync<UpdateBox>(app.Services);

        Assert.Equal(0, PageRenderer.Handlers<UpdateBox>(app.Services.GetRequiredService<UpdateService>(), nameof(UpdateService.Changed)));
    }

    // Starts the page while the sign-in waits, closes it, then lets the sign-in through and waits for the load to end.
    private static async Task CloseWhileSigningInAsync<TPage>(IServiceProvider app, IDictionary<string, object?>? parameters = null)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var signIn = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (page, rendered) = PageRenderer.StartHeld<TPage>(app, signIn.Task, parameters);
        page.Close();
        signIn.SetResult(PageRenderer.SignedIn(John, "John"));
        await rendered.WaitAsync(Cancel);
    }

    // An installation whose way in and sign-in are chosen and whose setup isn't done, so the guide stays open.
    private static TestWebApp SetupApp(string? boardType, Action<IServiceCollection>? configureServices = null)
    {
        var app = new TestWebApp(
            settings: new Dictionary<string, string?> { ["CloudflareAccess:TeamDomain"] = "myteam.cloudflareaccess.com", ["CloudflareAccess:Audience"] = "aud" },
            configureServices: configureServices,
            setUp: false);
        app.SaveBeforeStart(new Installation { AccessType = CloudflareMethods.TunnelId, SignInType = CloudflareMethods.AccessId }, boardType);
        return app;
    }

    // Signed in once, as anyone who opens the guide is.
    private static async Task SignInOnceAsync(TestWebApp app)
    {
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);
    }
}
