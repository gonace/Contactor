using System.Net;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// "Only on this network", and setup mode, answer only requests from this network: a machine with a public address
/// must not put the app on the internet directly.
/// </summary>
public sealed class ThisNetworkTests
{
    private const string Public = "203.0.113.5";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("169.254.0.0")]
    [InlineData("169.254.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%3")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fc00::")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    public void Should_CountAnAddress_OnALocalNetwork(string address)
    {
        Assert.True(ThisNetwork.Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("8.8.8.8")]
    [InlineData(Public)]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::2")]
    [InlineData("2001:db8::1")]
    [InlineData("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fec0::1")]
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe00::")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:100.64.0.1")]
    [InlineData("64:ff9b::c0a8:105")]
    public void Should_NotCountAnAddress_OutsideALocalNetwork(string address)
    {
        Assert.False(ThisNetwork.Contains(IPAddress.Parse(address)));
    }

    // Only "Only on this network", and setup mode whatever the way in: Cloudflare and Tailscale reach the app from
    // inside its container, and their sign-in decides.
    [Fact]
    public void Should_BeRequired_OnlyOnThisNetwork_AndInSetupMode()
    {
        foreach (var wayIn in AccessCatalogue.All)
        {
            Assert.Equal(wayIn.Id == AccessCatalogue.LocalId, ThisNetwork.Required(wayIn, signInChosen: true));
            Assert.True(ThisNetwork.Required(wayIn, signInChosen: false));
        }

        Assert.True(ThisNetwork.Required(wayIn: null, signInChosen: false));
        Assert.False(ThisNetwork.Required(wayIn: null, signInChosen: true));
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/relays")]
    [InlineData("GET", "/app.css")]
    [InlineData("POST", "/_blazor/negotiate?negotiateVersion=1")]
    [InlineData("POST", "/")]
    public async Task Should_RefuseARequestFromOutside_OnlyOnThisNetwork(string method, string path)
    {
        await using var app = OnlyOnThisNetwork(IPAddress.Parse(Public));
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await http.SendAsync(request, Cancel);

        await AssertRefusedAsync(response);
    }

    [Theory]
    [InlineData("192.168.1.5")]
    [InlineData("10.0.0.2")]
    [InlineData("172.17.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData(null)]
    public async Task Should_LetInARequestFromThisNetwork_OnlyOnThisNetwork(string? address)
    {
        await using var app = OnlyOnThisNetwork(address is null ? null : IPAddress.Parse(address));
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hi Someone", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    // Only the connection's own address counts: anyone can write these headers.
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Real-IP")]
    [InlineData("Cf-Connecting-IP")]
    [InlineData("Forwarded")]
    public async Task Should_RefuseARequestFromOutside_WhateverItsHeadersSay(string header)
    {
        await using var app = OnlyOnThisNetwork(IPAddress.Parse(Public));
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(header, header == "Forwarded" ? "for=192.168.1.5" : "192.168.1.5");
        using var response = await http.SendAsync(request, Cancel);

        await AssertRefusedAsync(response);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("100.64.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:203.0.113.5")]
    public async Task Should_RefuseTheSetupCodesPage_FromOutside(string address)
    {
        await using var app = new TestWebApp("Production", setUp: false) { RemoteAddress = IPAddress.Parse(address) };
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await http.GetAsync(SetupMode.CodePath, Cancel);
        using var guide = await http.GetAsync("/setup", Cancel);
        using var home = await http.GetAsync("/", Cancel);

        await AssertRefusedAsync(page);
        await AssertRefusedAsync(guide);
        await AssertRefusedAsync(home);
    }

    [Theory]
    [InlineData("192.168.1.5")]
    [InlineData("127.0.0.1")]
    [InlineData("172.17.0.1")]
    public async Task Should_ShowTheSetupCodesPage_OnThisNetwork(string address)
    {
        await using var app = new TestWebApp("Production", setUp: false) { RemoteAddress = IPAddress.Parse(address) };
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await http.GetAsync(SetupMode.CodePath, Cancel);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    // After reset-sign-in, the tunnel still runs: in setup mode the check holds for every way in. The tunnel itself
    // reaches the app from inside the container.
    [Fact]
    public async Task Should_RefuseTheSetupCodesPage_FromOutside_WhenTheSignInWasReset()
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings, setUp: false) { RemoteAddress = IPAddress.Parse(Public) };
        app.SaveBeforeStart(new Installation { AccessType = "cloudflare" });
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await http.GetAsync(SetupMode.CodePath, Cancel);

        await AssertRefusedAsync(page);
    }

    // Cloudflare: the tunnel's requests come from inside the container, but whoever it is, the sign-in decides.
    [Fact]
    public async Task Should_LeaveOtherWaysIn_ToTheirSignIn()
    {
        await using var app = new TestWebApp { RemoteAddress = IPAddress.Parse(Public) };
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(ThisNetwork.Refusal, await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    // Production Cloudflare without a token: denied by the sign-in, not by this check.
    [Fact]
    public async Task Should_LeaveARequestWithoutAToken_ToTheSignIn()
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings) { RemoteAddress = IPAddress.Parse(Public) };
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Access denied.", await response.Content.ReadAsStringAsync(Cancel));
    }

    // Still only this machine, from a LAN address or from outside.
    [Theory]
    [InlineData(Public)]
    [InlineData("192.168.1.5")]
    public async Task Should_KeepTheHealthCheck_ForThisMachineOnly(string address)
    {
        await using var app = OnlyOnThisNetwork(IPAddress.Parse(address));
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_KeepTheHealthCheck_ForThisMachine()
    {
        await using var app = OnlyOnThisNetwork(IPAddress.Loopback);
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The installer sees in the log that the port is open to the internet, without a scan of it filling the log.
    [Fact]
    public async Task Should_WarnAboutRequestsFromOutside_AtMostOnceAMinute()
    {
        var clock = new SetClock();
        var logs = new WarningCollector();
        await using var app = OnlyOnThisNetwork(
            IPAddress.Parse(Public),
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<ILoggerProvider>(logs);
            });
        using var http = app.CreateClient();

        await GetAsync();
        await GetAsync();
        await GetAsync();
        Assert.Single(logs.Warnings);
        Assert.Contains(Public, logs.Warnings[0], StringComparison.Ordinal);

        clock.Now += SignInMiddleware.OutsideWarningInterval - TimeSpan.FromSeconds(1);
        await GetAsync();
        Assert.Single(logs.Warnings);

        clock.Now += TimeSpan.FromSeconds(1);
        await GetAsync();
        await GetAsync();
        Assert.Equal(2, logs.Warnings.Count);

        // A clock set back doesn't hold the warnings back until it catches up.
        clock.Now -= TimeSpan.FromHours(1);
        await GetAsync();
        await GetAsync();
        Assert.Equal(3, logs.Warnings.Count);

        async Task GetAsync()
        {
            using var response = await http.GetAsync("/", Cancel);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    private static TestWebApp OnlyOnThisNetwork(IPAddress? remote, Action<IServiceCollection>? configureServices = null)
    {
        var app = new TestWebApp("Production", configureServices: configureServices, setUp: false) { RemoteAddress = remote };
        app.SaveBeforeStart(new Installation { SetupDoneUtc = DateTime.UtcNow, AccessType = "local", SignInType = "none" }, boardType: "vm201");
        return app;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ThisNetwork.Refusal, await response.Content.ReadAsStringAsync(Cancel));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // A clock that stands still until the test moves it; timers run on the real one.
    private sealed class SetClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // The warnings the sign-in middleware logs.
    private sealed class WarningCollector : ILoggerProvider, ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(SignInMiddleware).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (_warnings)
                {
                    _warnings.Add(formatter(state, exception));
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
