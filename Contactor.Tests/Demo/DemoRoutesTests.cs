using Contactor.Demo;
using Microsoft.AspNetCore.Http;

namespace Contactor.Tests.Demo;

/// <summary>How one host name reaches the page, the visitor's app and the visitor's simulator.</summary>
public sealed class DemoRoutesTests
{
    [Theory]
    [InlineData("GET", "/", "", "document", DemoTarget.Page, "/")]
    [InlineData("HEAD", "/", "", "document", DemoTarget.Page, "/")]
    [InlineData("GET", "/", "", "", DemoTarget.Page, "/")]
    [InlineData("GET", "/", "?demo-frame", "iframe", DemoTarget.App, "/")]
    [InlineData("GET", "/", "?demo-frame", "", DemoTarget.App, "/")]
    [InlineData("GET", "/", "", "iframe", DemoTarget.App, "/")]
    [InlineData("GET", "/", "", "empty", DemoTarget.App, "/")]
    [InlineData("POST", "/", "", "document", DemoTarget.App, "/")]
    [InlineData("GET", "/setup", "", "document", DemoTarget.App, "/setup")]
    [InlineData("GET", "/relay/3", "", "iframe", DemoTarget.App, "/relay/3")]
    [InlineData("GET", "/_blazor", "?id=1", "", DemoTarget.App, "/_blazor")]
    [InlineData("GET", "/demo/simulator/", "", "iframe", DemoTarget.Simulator, "/")]
    [InlineData("GET", "/demo/simulator", "", "iframe", DemoTarget.Simulator, "/")]
    [InlineData("GET", "/demo/simulator/_blazor", "?id=1", "", DemoTarget.Simulator, "/_blazor")]
    [InlineData("GET", "/Demo/Simulator/app.css", "", "style", DemoTarget.Simulator, "/app.css")]
    [InlineData("GET", "/demo/demo.css", "?v=1", "style", DemoTarget.Stylesheet, "/demo/demo.css")]
    public void Should_SendEachRequest_WhereItBelongs(string method, string path, string query, string destination, DemoTarget target, string forwarded)
    {
        var route = DemoRoutes.Find(Request(method, path, query, destination));

        Assert.Equal(target, route.Target);
        Assert.Equal(forwarded, route.Path.Value);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/HEALTHZ")]
    [InlineData("/healthz/")]
    [InlineData("/setup/reload")]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/demo/simulator/health")]
    [InlineData("/demo/simulator/alive")]
    [InlineData("/demo")]
    [InlineData("/demo/anything")]
    [InlineData("/demo/simulatorx")]
    public void Should_NotPassOn_WhatOnlyTheProgramsOwnMachineMayAsk(string path)
    {
        // The proxy connects from 127.0.0.1, so the app would take these for its own machine's.
        Assert.Equal(DemoTarget.NotFound, DemoRoutes.Find(Request("GET", path, "", "")).Target);
        Assert.Equal(DemoTarget.NotFound, DemoRoutes.Find(Request("POST", path, "", "")).Target);
    }

    [Fact]
    public void Should_PointTheSimulatorsBase_UnderItsPrefix()
    {
        var html = "<html><head><base href=\"/\" /><link href=\"app.css\"></head><body><base href=\"/\"></body></html>";

        Assert.Equal(
            "<html><head><base href=\"/demo/simulator/\" /><link href=\"app.css\"></head><body><base href=\"/\"></body></html>",
            DemoRoutes.RebaseSimulatorPage(html));
        Assert.Equal("<p>no base</p>", DemoRoutes.RebaseSimulatorPage("<p>no base</p>"));
    }

    [Theory]
    [InlineData("GET", "iframe", "", true)]
    [InlineData("GET", "document", "", true)]
    [InlineData("GET", "", "text/html,application/xhtml+xml", true)]
    [InlineData("GET", "script", "", false)]
    [InlineData("GET", "", "*/*", false)]
    [InlineData("POST", "iframe", "text/html", false)]
    public void Should_TellAPage_FromAFileOrAConnection(string method, string destination, string accept, bool document)
    {
        var request = Request(method, "/relay/1", "", destination);
        request.Headers.Accept = accept;

        Assert.Equal(document, DemoRoutes.IsDocument(request));
    }

    private static HttpRequest Request(string method, string path, string query, string destination)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query.Length == 0 ? null : query);
        if (destination.Length > 0)
        {
            context.Request.Headers["Sec-Fetch-Dest"] = destination;
        }

        return context.Request;
    }
}
