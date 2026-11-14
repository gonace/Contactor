using Contactor.Demo;
using Microsoft.AspNetCore.Http;

namespace Contactor.Tests.Demo;

public sealed class VisitorCookieTests
{
    [Fact]
    public void Should_GiveEachVisitor_ARandomIdOf128Bits()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => VisitorCookie.NewId()).ToList();

        Assert.All(ids, id => Assert.True(VisitorCookie.IsWellFormed(id)));
        Assert.All(ids, id => Assert.Equal(32, id.Length));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef/123456789abcdef")]
    public void Should_IgnoreACookie_TheDemoCouldNotHaveGiven(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{VisitorCookie.Name}={value}";

        Assert.Null(VisitorCookie.Read(context.Request));
    }

    [Fact]
    public void Should_ReadTheVisitorsId_AmongOtherCookies()
    {
        var id = VisitorCookie.NewId();
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $".AspNetCore.Antiforgery.x=abc; {VisitorCookie.Name}={id}";

        Assert.Equal(id, VisitorCookie.Read(context.Request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Should_GiveTheCookie_HttpOnlyAndLax_AndSecureOnlyOverHttps(bool https)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        var id = VisitorCookie.NewId();

        VisitorCookie.Give(context, id);

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{VisitorCookie.Name}={id}; path=/", header, StringComparison.Ordinal);
        Assert.Contains("httponly", header, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", header, StringComparison.Ordinal);
        Assert.Equal(https, header.Contains("secure", StringComparison.Ordinal));
        Assert.DoesNotContain("expires", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_LogOnlyTheStartOfAnId()
    {
        Assert.Equal("0123ab", VisitorCookie.ForLog("0123abcdef0123456789abcdef012345"));
    }
}
