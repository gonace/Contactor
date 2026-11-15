using System.Net;
using Contactor.Demo;
using Contactor.Tests.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Demo;

/// <summary>
/// The proxy with real copies standing in for the app and the simulator (small web servers on their ports): the page
/// and its cookie, the copy started by the frames, each visitor reaching only their own copy, the limit, activity and
/// ending, and what never reaches a copy.
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
    public async Task Should_GiveANewVisitor_ACookieAndThePage_AndStartNothingYet()
    {
        var client = await StartAsync();

        using var response = await client.SendAsync(Page(null), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("<iframe class=\"frame-simulator\" title=\"Simulator\" src=\"/demo/simulator/\"></iframe>", html, StringComparison.Ordinal);
        Assert.Contains("<iframe class=\"frame-app\" title=\"Contactor\" src=\"/?demo-frame\" inert></iframe>", html, StringComparison.Ordinal);

        // With the script, the frames load once it has sent the time zone, under "Getting your demo ready"; without it,
        // the ones in <noscript> above load at once, and the waiting screen doesn't show.
        Assert.Contains("<iframe class=\"frame-simulator\" title=\"Simulator\" data-src=\"/demo/simulator/\"></iframe>", html, StringComparison.Ordinal);
        Assert.Contains("<iframe class=\"frame-app\" title=\"Contactor\" data-src=\"/?demo-frame\" inert></iframe>", html, StringComparison.Ordinal);
        Assert.Matches("<noscript>\\s*<iframe class=\"frame-simulator\"", html);
        Assert.Contains("<noscript><style>.waiting, .demo-frames > iframe { display: none !important; } .demo-frames noscript { display: contents; }</style></noscript>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"waiting\" id=\"demo-waiting\" role=\"status\" aria-live=\"polite\">", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"demo-waiting-title\">Getting your demo ready</h2>", html, StringComparison.Ordinal);

        // Shown by the stylesheet after a while, so a page whose script didn't load isn't stuck on the waiting screen.
        Assert.Matches("<div class=\"waiting-slow\">\\s*<p class=\"muted\">Taking long\\?</p>\\s*<a class=\"button\" href=\"/\">Reload</a>", html);

        // The script says the copy didn't start, rather than that it ended, when that is what happened.
        Assert.Contains("data-not-started-title=\"Your copy of the demo didn't start\" data-not-started-text=\"Something went wrong on our side.\" data-not-started-button=\"Try again\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-zone-cookie=\"{VisitorTimeZone.CookieName}\"", html, StringComparison.Ordinal);
        Assert.Contains("reset when you're done", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody else", html, StringComparison.Ordinal);
        Assert.Contains("data-idle=\"900\" data-warning=\"120\" data-report=\"60\" data-start=\"135\" data-activity=\"/demo/activity\" data-timeout=\"/demo/timeout\"", html, StringComparison.Ordinal);
        Assert.Matches("<script src=\"/demo/demo.js\\?v=[0-9a-f]{12}\" defer></script>", html);
        Assert.Contains("role=\"alertdialog\"", html, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(VisitorId(response));

        // A bot or a link preview reads the page and nothing more: no copy, no place taken.
        Assert.Equal(0, Copies.Taken);
        Assert.Empty(Echo.Started);
    }

    [Fact]
    public async Task Should_StartTheCopy_OnTheFramesFirstRequest()
    {
        var client = await StartAsync();
        var id = await PageAsync(client);

        var app = await GetAsync(client, "/?demo-frame", id);

        Assert.Equal($"app {VisitorCookie.ForLog(id)} /?demo-frame cookie=[]", app);
        Assert.Equal(1, Copies.Count);
        Assert.Equal(2, Echo.Started.Count);
    }

    [Theory]
    [InlineData("America%2FNew_York", "America/New_York")]
    [InlineData("..%2F..%2Fetc%2Fpasswd", "UTC")]
    [InlineData(null, "UTC")]
    public async Task Should_StartTheCopy_InTheTimeZoneThePageSent(string? cookie, string timeZone)
    {
        var client = await StartAsync();
        var id = await PageAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/?demo-frame");
        request.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}" + (cookie is null ? "" : $"; {VisitorTimeZone.CookieName}={cookie}"));

        using var response = await client.SendAsync(request, Cancel);

        // Only the copy's start reads it; the app never gets the cookie.
        Assert.Equal($"app {VisitorCookie.ForLog(id)} /?demo-frame cookie=[]", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(timeZone, Echo.Started.Single(s => s.Name == "app").Environment["TimeZone"]);
    }

    [Fact]
    public async Task Should_SayTheCopyDidntStart_InTheFrame_WhenItFailedToStart()
    {
        var client = await StartAsync();
        Database.Fails = true;
        var id = await PageAsync(client);
        using var request = Get("/?demo-frame", id);
        request.Headers.Add("Sec-Fetch-Dest", "iframe");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Your copy of the demo didn't start", html, StringComparison.Ordinal);
        Assert.Contains("data-demo-copy=\"not-started\"", html, StringComparison.Ordinal);
        Assert.Empty(Echo.Started);
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
        var before = Echo.Requests;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/setup");
        if (id is not null)
        {
            request.Headers.Add("Cookie", $"{VisitorCookie.Name}={id}");
        }

        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var page = await client.SendAsync(request, Cancel);
        using var file = await client.SendAsync(Get("/_framework/blazor.web.js", id), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Your demo has ended", html, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\" action=\"/demo/end\" target=\"_top\">", html, StringComparison.Ordinal);
        Assert.Contains("Start again", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal("", await file.Content.ReadAsStringAsync(Cancel));

        // No copy got anything, and no new one started.
        Assert.Equal(before, Echo.Requests);
        Assert.Equal(2, Echo.Started.Count);
        Assert.Equal(1, Copies.Count);
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
        Assert.Contains("<form method=\"get\" action=\"/\" target=\"_top\">", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\">Try again now</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Copies.Count);

        // A visitor with a copy still gets theirs.
        using var own = await client.SendAsync(Page(first), Cancel);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // Once a copy is idle and stopped, the next visitor gets one.
        _time.Advance(TimeSpan.FromMinutes(17));
        await Copies.StopExpiredAsync();
        using var later = await client.SendAsync(Page(null), Cancel);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.NotNull(VisitorId(later));
    }

    [Fact]
    public async Task Should_ShowBusyInTheFrames_WhenTheDemoFilledUpAfterThePage()
    {
        var client = await StartAsync(maxVisitors: 1);
        var late = await PageAsync(client);
        await NewVisitorAsync(client);

        using var request = Get("/?demo-frame", late);
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var frame = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, frame.StatusCode);
        Assert.Contains("The demo is busy right now", await frame.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(1, Copies.Count);
    }

    [Fact]
    public async Task Should_KeepTheCopy_ForEachClickTheyReport()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);

        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(16));
            using var active = await client.SendAsync(Post("/demo/activity", id), Cancel);
            Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
            Assert.Equal(0, await Copies.StopExpiredAsync());
        }

        _time.Advance(TimeSpan.FromMinutes(17));
        Assert.Equal(1, await Copies.StopExpiredAsync());

        // The page hears it has ended.
        using var gone = await client.SendAsync(Post("/demo/activity", id), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "https://elsewhere.example")]
    [InlineData("Origin", "null")]
    public async Task Should_LetNoOtherSite_KeepOrEndACopy(string header, string value)
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        _time.Advance(TimeSpan.FromMinutes(10));

        using var activity = Post("/demo/activity", id);
        activity.Headers.Add(header, value);
        using var end = Post("/demo/end", id);
        end.Headers.Add(header, value);
        _time.Advance(TimeSpan.FromMinutes(6));
        using var timeout = Post("/demo/timeout", id);
        timeout.Headers.Add(header, value);
        using var activityResponse = await client.SendAsync(activity, Cancel);
        using var endResponse = await client.SendAsync(end, Cancel);
        using var timeoutResponse = await client.SendAsync(timeout, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, activityResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, endResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, timeoutResponse.StatusCode);
        Assert.Equal(1, Copies.Count);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await Copies.StopExpiredAsync());
    }

    [Fact]
    public async Task Should_OfferTheTemplate_OverTheApp_UntilTheVisitorGoesThroughTheSetup()
    {
        var client = await StartAsync();
        var id = await PageAsync(client);
        var first = await GetAsync(client, "/", id, document: true);
        await GetAsync(client, "/?demo-frame", id);

        Assert.Contains("Skip setup: start from a template", first, StringComparison.Ordinal);
        Assert.Contains("src=\"/?demo-frame\" inert>", first, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\" action=\"/demo/template\" target=\"_top\"", first, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"template\" value=\"workshop\">", first, StringComparison.Ordinal);
        Assert.Contains("Go through the setup myself", first, StringComparison.Ordinal);

        // The script's fetch, from the page.
        using var setup = Post("/demo/setup", id);
        setup.Headers.Add("Sec-Fetch-Site", "same-origin");
        setup.Headers.Add("Sec-Fetch-Dest", "empty");
        using var declined = await client.SendAsync(setup, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        var again = await GetAsync(client, "/", id, document: true);
        Assert.DoesNotContain("Skip setup", again, StringComparison.Ordinal);
        Assert.Contains("<iframe class=\"frame-app\" title=\"Contactor\" src=\"/?demo-frame\"></iframe>", again, StringComparison.Ordinal);
        Assert.Empty(Database.Templates);
    }

    [Fact]
    public async Task Should_ApplyTheTemplate_FromThePagesForm_AndShowThePageAgain()
    {
        var client = await StartAsync();
        var mine = await NewVisitorAsync(client);
        var theirs = await NewVisitorAsync(client);

        using var response = await client.SendAsync(TemplateForm(mine, "workshop"), Cancel);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        Assert.Single(Database.Templates);
        Assert.DoesNotContain("Skip setup", await GetAsync(client, "/", mine, document: true), StringComparison.Ordinal);
        Assert.Contains("Skip setup", await GetAsync(client, "/", theirs, document: true), StringComparison.Ordinal);

        // The app is started again, and the frame reaches it.
        Assert.Equal(3, Echo.Started.Count(s => s.Name == "app"));
        Assert.StartsWith("app ", await GetAsync(client, "/?demo-frame", mine), StringComparison.Ordinal);

        // Once only.
        using var twice = await client.SendAsync(TemplateForm(mine, "workshop"), Cancel);
        Assert.Equal(HttpStatusCode.SeeOther, twice.StatusCode);
        Assert.Single(Database.Templates);
    }

    [Theory]
    [InlineData("cross-site", "workshop", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", "garage", HttpStatusCode.BadRequest)]
    [InlineData("same-origin", "", HttpStatusCode.BadRequest)]
    public async Task Should_ApplyNoTemplate_FromAnotherSite_OrThatIsntOne(string site, string template, HttpStatusCode status)
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        using var request = TemplateForm(id, template);
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.Add("Sec-Fetch-Site", site);
        using var setup = Post("/demo/setup", id);
        setup.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request, Cancel);
        using var setupResponse = await client.SendAsync(setup, Cancel);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, setupResponse.StatusCode);
        Assert.Empty(Database.Templates);
        Assert.Contains("Skip setup", await GetAsync(client, "/", id, document: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_EndOnlyTheVisitorsOwnCopy_AndStartAgainWithANewOne()
    {
        var client = await StartAsync();
        var mine = await NewVisitorAsync(client);
        var theirs = await NewVisitorAsync(client);

        // "Start again": a form's POST, from the page.
        using var end = Post("/demo/end", mine);
        end.Headers.Add("Sec-Fetch-Site", "same-origin");
        end.Headers.Add("Sec-Fetch-Dest", "document");
        end.Headers.Add("Origin", "http://localhost");
        using var ended = await client.SendAsync(end, Cancel);

        Assert.Equal(HttpStatusCode.SeeOther, ended.StatusCode);
        Assert.Equal("/", ended.Headers.Location?.ToString());
        Assert.Equal(1, Copies.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Post("/demo/activity", theirs), Cancel)).StatusCode);

        // The page again: a new id, never the old one, and a fresh copy once its frames ask.
        using var again = await client.SendAsync(Page(mine), Cancel);
        var newId = VisitorId(again);
        Assert.NotNull(newId);
        Assert.NotEqual(mine, newId);
        Assert.StartsWith("app ", await GetAsync(client, "/?demo-frame", newId), StringComparison.Ordinal);
        Assert.Equal(2, Copies.Count);
    }

    [Fact]
    public async Task Should_EndTheCopy_WhenThePagesCountDownRunsOut()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        _time.Advance(TimeSpan.FromMinutes(17));

        using var ended = await client.SendAsync(Timeout(id), Cancel);

        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(0, Copies.Taken);

        // Another tab's count down, later: nothing is left, so it says the copy ended too.
        using var again = await client.SendAsync(Timeout(id), Cancel);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Should_KeepTheCopy_WhenThePagesCountDownRunsOut_WhileAnotherTabIsUsed()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        _time.Advance(TimeSpan.FromMinutes(10));
        using var active = await client.SendAsync(Post("/demo/activity", id), Cancel);
        _time.Advance(TimeSpan.FromMinutes(7));

        using var kept = await client.SendAsync(Timeout(id), Cancel);

        Assert.Equal(HttpStatusCode.Conflict, kept.StatusCode);
        Assert.Equal(1, Copies.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Post("/demo/activity", id), Cancel)).StatusCode);
    }

    [Fact]
    public async Task Should_EndTheCopy_OnStartAgain_EvenWhileAnotherTabIsUsed()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        using var active = await client.SendAsync(Post("/demo/activity", id), Cancel);

        using var end = Post("/demo/end", id);
        end.Headers.Add("Sec-Fetch-Site", "same-origin");
        end.Headers.Add("Sec-Fetch-Dest", "document");
        using var ended = await client.SendAsync(end, Cancel);

        Assert.Equal(HttpStatusCode.SeeOther, ended.StatusCode);
        Assert.Equal(0, Copies.Taken);
    }

    [Fact]
    public async Task Should_ShowEnded_ToARequestThatWaitedForACopyThatEnded()
    {
        var client = await StartAsync(listens: false);
        var id = await PageAsync(client);
        using var request = Get("/setup", id);
        request.Headers.Add("Sec-Fetch-Dest", "iframe");

        var waiting = client.SendAsync(request, Cancel);
        await Wait.UntilAsync(() => Copies.Count == 1);
        await Copies.EndAsync(id);
        using var response = await waiting;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Your demo has ended", html, StringComparison.Ordinal);
        Assert.Contains("data-demo-copy=\"ended\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_GiveAVisitorWhoseCopyWasStopped_ANewId()
    {
        var client = await StartAsync();
        var id = await NewVisitorAsync(client);
        _time.Advance(TimeSpan.FromMinutes(17));
        await Copies.StopExpiredAsync();

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
        Assert.Equal(0, Copies.KeptCount);
        Assert.Equal(0, Copies.Taken);
    }

    [Theory]
    [InlineData("/demo/demo.css?v=1", "text/css", "--accent:")]
    [InlineData("/demo/demo.js?v=1", "text/javascript", "body.dataset.activity")]
    public async Task Should_ServeThePagesFiles(string path, string type, string content)
    {
        var client = await StartAsync();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(content, await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(0, Copies.Taken);
    }

    [Theory]
    [InlineData("ibm-plex-sans-latin-400-normal.woff2")]
    [InlineData("ibm-plex-sans-latin-600-normal.woff2")]
    public async Task Should_ServeTheAppsFonts_TheStylesheetUses(string font)
    {
        var client = await StartAsync();
        var css = await client.GetStringAsync(new Uri("/demo/demo.css", UriKind.Relative), Cancel);

        using var response = await client.GetAsync(new Uri("/demo/fonts/" + font, UriKind.Relative), Cancel);

        Assert.Contains($"url(\"fonts/{font}\")", css, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("font/woff2", response.Content.Headers.ContentType?.MediaType);
        var app = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web", "wwwroot", "fonts", font), Cancel);
        Assert.Equal(app, await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    [Theory]
    [InlineData("/demo/fonts/OFL.txt")]
    [InlineData("/demo/fonts/")]
    [InlineData("/demo/activity")]
    [InlineData("/demo/end")]
    [InlineData("/demo/timeout")]
    public async Task Should_NotFind_AnythingElseOfTheDemos(string path)
    {
        var client = await StartAsync();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("172.30.60.2", "https", true)]
    [InlineData("::ffff:172.30.60.2", "https", true)]
    [InlineData("172.30.60.2", "http", false)]
    [InlineData("172.30.60.2", null, false)]
    [InlineData("172.30.60.3", "https", false)]
    [InlineData("127.0.0.1", "https", false)]
    public async Task Should_MarkTheCookieSecure_OnlyWhenTheTunnelSaysTheVisitorCameOverHttps(string from, string? proto, bool secure)
    {
        await StartAsync(tunnel: "172.30.60.2");

        var context = await PageFromAsync(from, proto);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(VisitorCookie.Name + "=", cookie, StringComparison.Ordinal);
        Assert.Equal(secure, cookie.Contains("secure", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("172.30.60.2")]
    [InlineData("127.0.0.1")]
    public async Task Should_BelieveNoOne_AboutHttps_WithoutATunnelAddress(string from)
    {
        await StartAsync();

        var context = await PageFromAsync(from, "https");

        Assert.DoesNotContain("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    [InlineData("::1", HttpStatusCode.OK)]
    [InlineData("172.30.60.2", HttpStatusCode.NotFound)]
    public async Task Should_AnswerTheHealthCheck_OnItsOwnMachineOnly(string from, HttpStatusCode status)
    {
        await StartAsync(tunnel: "172.30.60.2");

        var context = await _app!.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = DemoServiceCollectionExtensions.HealthPath;
            c.Connection.RemoteIpAddress = IPAddress.Parse(from);
        }, Cancel);

        Assert.Equal((int)status, context.Response.StatusCode);
        Assert.Equal(0, Copies.KeptCount);
        Assert.Equal(0, Copies.Taken);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private VisitorCopies Copies => _app!.Services.GetRequiredService<VisitorCopies>();

    private FakeCopyDatabase Database => (FakeCopyDatabase)_app!.Services.GetRequiredService<ICopyDatabase>();

    private EchoStarter Echo => (EchoStarter)_app!.Services.GetRequiredService<IProgramStarter>();

    // The page from a connection of this address, with this X-Forwarded-Proto.
    private async Task<HttpContext> PageFromAsync(string from, string? proto)
    {
        return await _app!.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/";
            c.Request.Headers["Sec-Fetch-Dest"] = "document";
            if (proto is not null)
            {
                c.Request.Headers["X-Forwarded-Proto"] = proto;
            }

            c.Connection.RemoteIpAddress = IPAddress.Parse(from);
        }, Cancel);
    }

    private async Task<HttpClient> StartAsync(int maxVisitors = 10, bool listens = true, string tunnel = "")
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
            ["Demo:TunnelAddress"] = tunnel,
        });
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<IProgramStarter>(new EchoStarter { Listens = listens });
        builder.Services.AddSingleton<IFreePorts, LoopbackPorts>();
        builder.Services.AddSingleton<ICopyDatabase, FakeCopyDatabase>();
        builder.Services.AddDemo(builder.Configuration);

        // The tests look for expired copies themselves, not on the clock they move.
        builder.Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        _app = builder.Build();
        _app.MapDemo();
        await _app.StartAsync(Cancel);
        _client = _app.GetTestClient();
        return _client;
    }

    // The page only: an id, no copy yet.
    private static async Task<string> PageAsync(HttpClient client)
    {
        using var response = await client.SendAsync(Page(null), Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return VisitorId(response) ?? throw new InvalidOperationException("No cookie.");
    }

    // A visitor as a browser is one: the page, then its frames, which start the copy.
    private static async Task<string> NewVisitorAsync(HttpClient client)
    {
        var id = await PageAsync(client);
        await GetAsync(client, "/?demo-frame", id);
        return id;
    }

    private static async Task<string> GetAsync(HttpClient client, string path, string id, bool document = false)
    {
        using var request = Get(path, id);
        if (document)
        {
            request.Headers.Add("Sec-Fetch-Dest", "document");
        }

        using var response = await client.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static HttpRequestMessage Page(string? id)
    {
        var request = Get("/", id);
        request.Headers.Add("Sec-Fetch-Dest", "document");
        return request;
    }

    // The page's count down: a fetch from the page.
    private static HttpRequestMessage Timeout(string id)
    {
        var request = Post("/demo/timeout", id);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        return request;
    }

    // "Skip setup": the page's form.
    private static HttpRequestMessage TemplateForm(string id, string template)
    {
        var request = Post("/demo/template", id);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("template", template)]);
        return request;
    }

    private static HttpRequestMessage Post(string path, string? id)
    {
        var request = Get(path, id);
        request.Method = HttpMethod.Post;
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
