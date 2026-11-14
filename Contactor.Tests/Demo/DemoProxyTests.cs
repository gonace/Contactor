using System.Net;
using Contactor.Demo;
using Contactor.Tests.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Demo;

/// <summary>
/// The proxy with real copies standing in for the app and the simulator (small web servers on their ports): the page
/// and its cookie, each visitor reaching only their own copy, the limit, and what never reaches a copy.
/// </summary>
public sealed class DemoProxyTests : IAsyncDisposable
{
    private readonly DemoFolder _folder = new();
    private readonly ManualTimeProvider _time = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _folder.Dispose();
    }

    [Fact]
    public async Task Should_GiveANewVisitor_ACopyACookieAndThePage()
    {
        var client = await StartAsync();

        using var response = await client.SendAsync(Page(null), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("<iframe class=\"frame-simulator\" title=\"Simulator\" src=\"/demo/simulator/\"></iframe>", html, StringComparison.Ordinal);
        Assert.Contains("<iframe class=\"frame-app\" title=\"Contactor\" src=\"/?demo-frame\"></iframe>", html, StringComparison.Ordinal);
        Assert.Contains("deleted after 30 minutes without use", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var id = VisitorId(response);
        Assert.NotNull(id);
        Assert.Equal(1, Copies.Count);
    }

    [Fact]
    public async Task Should_KeepAVisitorsCopy_OnTheirNextVisit()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);

        using var again = await client.SendAsync(Page(id), Cancel);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(VisitorId(again));
        Assert.Equal(1, Copies.Count);
    }

    [Fact]
    public async Task Should_SendEachVisitor_ToTheirOwnCopyOnly()
    {
        var client = await StartAsync();
        var first = await NewVisitorAsync(client);
        var second = await NewVisitorAsync(client);

        var firstApp = await GetAsync(client, "/relay/3?x=1", first);
        var secondApp = await GetAsync(client, "/relay/3?x=1", second);
        var firstSimulator = await GetAsync(client, "/demo/simulator/_blazor/negotiate", first);

        Assert.Equal($"app {VisitorCookie.ForLog(first)} /relay/3?x=1 cookie=[]", firstApp);
        Assert.Equal($"app {VisitorCookie.ForLog(second)} /relay/3?x=1 cookie=[]", secondApp);
        Assert.Equal($"simulator {VisitorCookie.ForLog(first)} /_blazor/negotiate cookie=[]", firstSimulator);
    }

    [Fact]
    public async Task Should_KeepTheAppsOwnCookies_AndLeaveTheDemosOut()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        request.Headers.Add("Cookie", $".AspNetCore.Antiforgery.a=1; {VisitorCookie.Name}={id}; other=2");

        using var response = await client.SendAsync(request, Cancel);

        Assert.EndsWith("cookie=[.AspNetCore.Antiforgery.a=1; other=2]", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PointTheSimulatorsPage_BackThroughTheProxy()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo/simulator/");
        request.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}");
        request.Headers.Add("Accept", "text/html");
        request.Headers.Add("Accept-Encoding", "gzip");

        using var response = await client.SendAsync(request, Cancel);

        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Equal("<html><head><base href=\"/demo/simulator/\"><title>simulator</title></head><body>simulator page</body></html>", html);
        Assert.Equal(html.Length, response.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("../../../../etc/passwd")]
    public async Task Should_SendNoRequest_WithoutACopyOfTheVisitorsOwn(string? id)
    {
        var client = await StartAsync();
        await NewVisitorAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/setup");
        if (id is not null)
        {
            request.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}");
        }

        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var page = await client.SendAsync(request, Cancel);
        using var file = await client.SendAsync(Get("/_framework/blazor.web.js", id), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Contains("This copy of the demo was stopped", await page.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Contains("target=\"_top\"", await page.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal("", await file.Content.ReadAsStringAsync(Cancel));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/setup/reload")]
    [InlineData("/demo/simulator/health")]
    public async Task Should_NotPassOn_WhatOnlyTheProgramsOwnMachineMayAsk(string path)
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);

        using var get = await client.SendAsync(Get(path, id), Cancel);
        using var post = new HttpRequestMessage(HttpMethod.Post, path);
        post.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}");
        using var posted = await client.SendAsync(post, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Equal("", await get.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Should_ShowTheBusyPage_WhenEveryCopyIsInUse()
    {
        var client = await StartAsync(maxVisitors: 2);
        var first = await NewVisitorAsync(client);
        await NewVisitorAsync(client);

        using var busy = await client.SendAsync(Page(null), Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("30", busy.Headers.RetryAfter?.ToString());
        Assert.Null(VisitorId(busy));
        var html = await busy.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("<h1>The demo is busy right now</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"30\">", html, StringComparison.Ordinal);
        Assert.Contains("<form method=\"get\" action=\"/\">", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\">Try again now</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Copies.Count);

        // A visitor with a copy still gets theirs.
        using var own = await client.SendAsync(Page(first), Cancel);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // Once a copy is idle and stopped, the next visitor gets one.
        _time.Advance(TimeSpan.FromMinutes(30));
        await Copies.StopIdleAsync();
        using var later = await client.SendAsync(Page(null), Cancel);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.NotNull(VisitorId(later));
    }

    [Fact]
    public async Task Should_GiveAVisitorWhoseCopyWasStopped_ANewCopyAndANewId()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        _time.Advance(TimeSpan.FromMinutes(30));
        await Copies.StopIdleAsync();

        using var response = await client.SendAsync(Page(id), Cancel);

        var newId = VisitorId(response);
        Assert.NotNull(newId);
        Assert.NotEqual(id, newId);
    }

    [Fact]
    public async Task Should_StartNoCopy_ForACheckThatTheSiteAnswers()
    {
        var client = await StartAsync();
        using var request = Page(null);
        request.Method = HttpMethod.Head;

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(VisitorId(response));
        Assert.Equal(0, Copies.Count);
    }

    [Fact]
    public async Task Should_ServeTheStylesheet()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync(new Uri("/demo/demo.css?v=1", UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--accent:", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(0, Copies.Count);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private VisitorCopies Copies => _app!.Services.GetRequiredService<VisitorCopies>();

    private async Task<HttpClient> StartAsync(int maxVisitors = 10)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:AppPath"] = _folder.AppPath,
            ["Demo:SimulatorPath"] = _folder.SimulatorPath,
            ["Demo:DataRoot"] = _folder.DataRoot,
            ["Demo:MaxVisitors"] = maxVisitors.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<IProgramStarter, EchoStarter>();
        builder.Services.AddSingleton<IFreePorts, LoopbackPorts>();
        builder.Services.AddSingleton<ICopyDatabase, FakeCopyDatabase>();
        builder.Services.AddDemo(builder.Configuration);
        _app = builder.Build();
        _app.MapDemo();
        await _app.StartAsync(Cancel);
        _client = _app.GetTestClient();
        return _client;
    }

    private static async Task<string> NewVisitorAsync(HttpClient client)
    {
        using var response = await client.SendAsync(Page(null), Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return VisitorId(response) ?? throw new InvalidOperationException("No cookie.");
    }

    private static async Task<string> GetAsync(HttpClient client, string path, string id)
    {
        using var response = await client.SendAsync(Get(path, id), Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static HttpRequestMessage Page(string? id)
    {
        var request = Get("/", id);
        request.Headers.Add("Sec-Fetch-Dest", "document");
        return request;
    }

    private static HttpRequestMessage Get(string path, string? id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (id is not null)
        {
            request.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}");
        }

        return request;
    }

    private static string? VisitorId(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(VisitorCookie.Name + "=", StringComparison.Ordinal))
                .Select(v => v[(VisitorCookie.Name.Length + 1)..]).SingleOrDefault()
            : null;
    }
}
