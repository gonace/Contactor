using System.Net;
using System.Net.Sockets;
using Contactor.Web.Features.Tailscale;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>Who Tailscale identity says a request is from: the person Tailscale Serve names, and only through Serve.</summary>
public sealed class TailscaleIdentityTests : IDisposable
{
    // Serve's socket, and another, bound in a folder of their own as Kestrel binds the app's.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ct-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Socket _appSocket;
    private readonly Socket _otherSocket;

    public TailscaleIdentityTests()
    {
        Directory.CreateDirectory(_folder);
        _appSocket = Bind(Path.Combine(_folder, "app.sock"));
        _otherSocket = Bind(Path.Combine(_folder, "other.sock"));
    }

    private string AppSocketPath => Path.Combine(_folder, "app.sock");

    public void Dispose()
    {
        _appSocket.Dispose();
        _otherSocket.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LetInThePersonTailscaleServeNames_OnTheAppsSocket()
    {
        var context = Request(_appSocket, "Alice@Example.com");

        Assert.Equal("alice@example.com", await Identity().AuthenticateAsync(context));
    }

    [Fact]
    public async Task Should_NotBelieveTheHeader_FromThisMachinesOwnAddress()
    {
        // tailscaled passes connections from the Tailscale network to 127.0.0.1, past Serve: anyone there could
        // write the header.
        var context = Request(socket: null, "alice@example.com");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;

        Assert.Null(await Identity().AuthenticateAsync(context));
    }

    [Fact]
    public async Task Should_NotBelieveTheHeader_OnAnotherSocket()
    {
        Assert.Null(await Identity().AuthenticateAsync(Request(_otherSocket, "alice@example.com")));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithoutTheAppsSocketSet()
    {
        Assert.Null(await Identity(appSocket: "").AuthenticateAsync(Request(_appSocket, "alice@example.com")));
    }

    // Kestrel as the app runs it: the TCP port and Serve's socket. Only the socket's requests are believed.
    [Fact]
    public async Task Should_BelieveOnlyTheSocket_InKestrel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ct-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, "app.sock");
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0", $"http://unix:{socket}");
            await using var app = builder.Build();
            var identity = Identity(socket);
            app.MapGet("/", async (HttpContext context) => await identity.AuthenticateAsync(context) ?? "denied");
            await app.StartAsync(Cancel);
            var tcp = app.Urls.Single(u => u.StartsWith("http://127.0.0.1", StringComparison.Ordinal));

            using var overTcp = new HttpClient();
            using var overSocket = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await unix.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancellationToken);
                    return new NetworkStream(unix, ownsSocket: true);
                },
            });

            Assert.Equal("alice@example.com", await Get(overSocket, "http://relays.tail1234.ts.net/"));
            Assert.Equal("denied", await Get(overTcp, tcp + "/"));
            await app.StopAsync(Cancel);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static async Task<string> Get(HttpClient client, string url)
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
        Assert.Null(await Identity().AuthenticateAsync(Request(_appSocket, login: null)));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithTheHeaderTwice()
    {
        var context = Request(_appSocket, "alice@example.com");
        context.Request.Headers.Append(TailscaleIdentity.HeaderName, "bob@example.com");

        Assert.Null(await Identity().AuthenticateAsync(context));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Should_LetNobodyIn_WithALoginThatIsNoAddress(string login)
    {
        Assert.Null(await Identity().AuthenticateAsync(Request(_appSocket, login)));
    }

    [Fact]
    public async Task Should_TakeAGitHubLogin_AsTailscaleWritesIt()
    {
        Assert.Equal("alice@github", await Identity().AuthenticateAsync(Request(_appSocket, "alice@github")));
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

    private TailscaleIdentity Identity(string? appSocket = null) =>
        new(Options.Create(new TailscaleOptions { AppSocket = appSocket ?? AppSocketPath }));

    private static Socket Bind(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        return socket;
    }

    // A request as Kestrel gives it: on a socket, or (null) on a TCP port.
    private static DefaultHttpContext Request(Socket? socket, string? login)
    {
        var context = new DefaultHttpContext();
        if (socket is not null)
        {
            context.Features.Set<IConnectionSocketFeature>(new ConnectionSocket(socket));
        }

        if (login is not null)
        {
            context.Request.Headers[TailscaleIdentity.HeaderName] = login;
        }

        return context;
    }

    private sealed class ConnectionSocket : IConnectionSocketFeature
    {
        public ConnectionSocket(Socket socket)
        {
            Socket = socket;
        }

        public Socket Socket { get; }
    }
}
