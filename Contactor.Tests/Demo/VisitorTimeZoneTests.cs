using Contactor.Demo;
using Microsoft.AspNetCore.Http;

namespace Contactor.Tests.Demo;

/// <summary>The visitor's time zone, as the page's script sends it: a real IANA name, or UTC.</summary>
public sealed class VisitorTimeZoneTests
{
    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/Stockholm")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/GMT+5")]
    [InlineData("UTC")]
    public void Should_TakeATimeZone_ThisMachineKnows(string name)
    {
        Assert.Equal(name, VisitorTimeZone.Find(name).Id);
    }

    [Fact]
    public void Should_ReadTheCookie_TheScriptWrites()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"other=1; {VisitorTimeZone.CookieName}=America%2FNew_York";

        Assert.Equal("America/New_York", VisitorTimeZone.Read(context.Request).Id);
    }

    [Fact]
    public void Should_RunInUtc_WithoutTheCookie()
    {
        Assert.Same(TimeZoneInfo.Utc, VisitorTimeZone.Read(new DefaultHttpContext().Request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("europe/nowhere")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("../../../etc/passwd")]
    [InlineData("/usr/share/zoneinfo/Europe/Stockholm")]
    [InlineData("Europe/../Europe/Stockholm")]
    [InlineData("Europe//Stockholm")]
    [InlineData("Europe/Stockholm/")]
    [InlineData("Europe\\Stockholm")]
    [InlineData("Europe/Stockholm\n")]
    [InlineData("Europe/Stockholm\0")]
    [InlineData("Europe/Stockholm\u0007")]
    [InlineData("Europe/Stockholm; path=/")]
    [InlineData("Europe/Stockhölm")]
    [InlineData("-Europe/Stockholm")]
    public void Should_RunInUtc_OnANameThatIsntATimeZone(string? name)
    {
        Assert.Same(TimeZoneInfo.Utc, VisitorTimeZone.Find(name));
    }

    [Fact]
    public void Should_RunInUtc_OnANameTooLong()
    {
        Assert.Same(TimeZoneInfo.Utc, VisitorTimeZone.Find("America/" + new string('a', 10_000)));
        Assert.Same(TimeZoneInfo.Utc, VisitorTimeZone.Find(string.Join('/', Enumerable.Repeat("Europe", 11))));
    }
}
