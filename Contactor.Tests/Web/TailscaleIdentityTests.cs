using System.Net;
using Contactor.Web.Features.Tailscale;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>Who Tailscale identity says a request is from: the person Tailscale Serve names, and only through Serve.</summary>
public sealed class TailscaleIdentityTests
{
    private const string ServeAddress = "127.0.0.2:8081";

    private static readonly IPEndPoint Serve = IPEndPoint.Parse(ServeAddress);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LetInThePersonTailscaleServeNames_OnServesAddress()
    {
        var context = Request(Serve, "Alice@Example.com");

        Assert.Equal("alice@example.com", await Identity().AuthenticateAsync(context));
    }

    [Fact]
    public async Task Should_NotBelieveTheHeader_FromThisMachinesOwnAddress()
    {
        // tailscaled passes connections from the Tailscale network to 127.0.0.1, past Serve: anyone there could
        // write the header.
        var context = Request(new IPEndPoint(IPAddress.Loopback, Serve.Port), "alice@example.com");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.Null(await Identity().AuthenticateAsync(context));
    }

    [Theory]
    [InlineData("127.0.0.2", 8080)]
    [InlineData("127.0.0.1", 8081)]
    [InlineData("::ffff:127.0.0.1", 8081)]
    [InlineData("::1", 8081)]
    public async Task Should_NotBelieveTheHeader_OnAnotherAddress(string address, int port)
    {
        Assert.Null(await Identity().AuthenticateAsync(Request(new IPEndPoint(IPAddress.Parse(address), port), "alice@example.com")));
    }

    [Fact]
    public async Task Should_BelieveTheHeader_OnServesAddressAsIPv6MappedToIPv4()
    {
        var mapped = new IPEndPoint(Serve.Address.MapToIPv6(), Serve.Port);

        Assert.Equal("alice@example.com", await Identity().AuthenticateAsync(Request(mapped, "alice@example.com")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8081")]
    public async Task Should_LetNobodyIn_WithoutAServeAddressOnlyServeReaches(string serveAddress)
    {
        var local = IPEndPoint.Parse(serveAddress.Length > 0 ? serveAddress : ServeAddress);

        Assert.Null(await Identity(serveAddress).AuthenticateAsync(Request(local, "alice@example.com")));
    }

    // Kestrel as the app runs it: its port, and Serve's address. Only requests on Serve's address are believed.
    [Fact]
    public async Task Should_BelieveOnlyServesAddress_InKestrel()
    {
        // macOS has only 127.0.0.1 until another loopback address is added (sudo ifconfig lo0 alias 127.0.0.2).
        Assert.SkipUnless(CanListenOn(IPAddress.Parse("127.0.0.2")), "This computer can't listen on 127.0.0.2.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0", "http://127.0.0.2:0");
        await using var app = builder.Build();
        TailscaleIdentity? identity = null;
        app.MapGet("/", async (HttpContext context) => await identity!.AuthenticateAsync(context) ?? "denied");
        await app.StartAsync(Cancel);
        var serve = app.Urls.Single(u => u.StartsWith("http://127.0.0.2", StringComparison.Ordinal));
        var other = app.Urls.Single(u => u.StartsWith("http://127.0.0.1", StringComparison.Ordinal));
        identity = Identity(new Uri(serve).Authority);

        using var client = new HttpClient();
        Assert.Equal("alice@example.com", await GetAsync(client, serve + "/"));
        Assert.Equal("denied", await GetAsync(client, other + "/"));
        await app.StopAsync(Cancel);

        static async Task<string> GetAsync(HttpClient client, string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TailscaleIdentity.HeaderName, "alice@example.com");
            using var response = await client.SendAsync(request, Cancel);
            return await response.Content.ReadAsStringAsync(Cancel);
        }
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithoutTheHeader()
    {
        // Tagged devices and Funnel: Serve sends no person.
        Assert.Null(await Identity().AuthenticateAsync(Request(Serve, login: null)));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithTheHeaderTwice()
    {
        var context = Request(Serve, "alice@example.com");
        context.Request.Headers.Append(TailscaleIdentity.HeaderName, "bob@example.com");

        Assert.Null(await Identity().AuthenticateAsync(context));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Should_LetNobodyIn_WithALoginThatIsNoAddress(string login)
    {
        Assert.Null(await Identity().AuthenticateAsync(Request(Serve, login)));
    }

    [Fact]
    public async Task Should_TakeAGitHubLogin_AsTailscaleWritesIt()
    {
        Assert.Equal("alice@github", await Identity().AuthenticateAsync(Request(Serve, "alice@github")));
    }

    [Theory]
    [InlineData("alice@example.com", "alice@example.com")]
    [InlineData("=?utf-8?q?j=C3=B6rgen@example.com?=", "jörgen@example.com")]
    [InlineData("=?UTF-8?Q?j=C3=B6rgen_?= =?utf-8?q?s@example.com?=", "jörgen s@example.com")]
    public void Should_DecodeTheLogin_AsTailscaleEncodesIt(string header, string login)
    {
        Assert.Equal(login, TailscaleIdentity.Decode(header));
    }

    [Theory]
    [InlineData("=?utf-8?q?j=C3=B6rgen@example.com")]
    [InlineData("=?utf-8?b?am9yZ2Vu?=")]
    [InlineData("=?iso-8859-1?q?j=F6rgen?=")]
    [InlineData("=?utf-8?q?j=C3?=")]
    [InlineData("=?utf-8?q?j=C?=")]
    [InlineData("=?utf-8?q?j=ZZ?=")]
    [InlineData("=?utf-8?q?j?x?=")]
    [InlineData("=?utf-8?q??=")]
    [InlineData("=?utf-8?q?a=0Ab@example.com?=")]
    [InlineData("=?utf-8?q?alice@example.com?=")]
    [InlineData("jörgen@example.com")]
    [InlineData("alice@example.com\u0001")]
    public void Should_ReadNoLogin_FromAnythingElse(string header)
    {
        Assert.Null(TailscaleIdentity.Decode(header));
    }

    private static TailscaleIdentity Identity(string? serveAddress = null) =>
        new(Options.Create(new TailscaleOptions { ServeAddress = serveAddress ?? ServeAddress }));

    // A request as Kestrel gives it, on the connection's own end.
    private static DefaultHttpContext Request(IPEndPoint local, string? login)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = local.Address;
        context.Connection.LocalPort = local.Port;
        if (login is not null)
        {
            context.Request.Headers[TailscaleIdentity.HeaderName] = login;
        }

        return context;
    }

    private static bool CanListenOn(IPAddress address)
    {
        using var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
