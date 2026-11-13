using System.Net;
using Contactor.Web.Features.Tailscale;
using Microsoft.AspNetCore.Http;

namespace Contactor.Tests.Web;

/// <summary>Who Tailscale identity says a request is from: the person Tailscale Serve names, and only through Serve.</summary>
public sealed class TailscaleIdentityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Should_LetInThePersonTailscaleServeNames(string from)
    {
        var context = Request(from, "Alice@Example.com");

        Assert.Equal("alice@example.com", await new TailscaleIdentity().AuthenticateAsync(context));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("100.101.102.103")]
    [InlineData("172.17.0.1")]
    [InlineData("fd7a:115c:a1e0::1")]
    public async Task Should_NotBelieveTheHeader_FromAnywhereButThisMachine(string from)
    {
        // Anyone who reaches the app's port directly could write the header: only Tailscale Serve, on the machine
        // itself, is believed.
        var context = Request(from, "alice@example.com");

        Assert.Null(await new TailscaleIdentity().AuthenticateAsync(context));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithoutTheHeader()
    {
        // Tagged devices and Funnel: Serve sends no person.
        Assert.Null(await new TailscaleIdentity().AuthenticateAsync(Request("127.0.0.1", login: null)));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithTheHeaderTwice()
    {
        var context = Request("127.0.0.1", "alice@example.com");
        context.Request.Headers.Append(TailscaleIdentity.HeaderName, "bob@example.com");

        Assert.Null(await new TailscaleIdentity().AuthenticateAsync(context));
    }

    [Fact]
    public async Task Should_LetNobodyIn_WithoutAnAddress()
    {
        var context = Request("127.0.0.1", "alice@example.com");
        context.Connection.RemoteIpAddress = null;

        Assert.Null(await new TailscaleIdentity().AuthenticateAsync(context));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Should_LetNobodyIn_WithALoginThatIsNoAddress(string login)
    {
        Assert.Null(await new TailscaleIdentity().AuthenticateAsync(Request("127.0.0.1", login)));
    }

    [Fact]
    public async Task Should_TakeAGitHubLogin_AsTailscaleWritesIt()
    {
        Assert.Equal("alice@github", await new TailscaleIdentity().AuthenticateAsync(Request("127.0.0.1", "alice@github")));
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
    [InlineData("jörgen@example.com")]
    [InlineData("alice@example.com\u0001")]
    public void Should_ReadNoLogin_FromAnythingElse(string header)
    {
        Assert.Null(TailscaleIdentity.Decode(header));
    }

    private static DefaultHttpContext Request(string from, string? login)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(from);
        if (login is not null)
        {
            context.Request.Headers[TailscaleIdentity.HeaderName] = login;
        }

        return context;
    }
}
