using Contactor.Boards.Vm204;

namespace Contactor.Tests.Vm204;

/// <summary>The VM204's requests and answers in plain words, with the key and the login never shown.</summary>
public sealed class Vm204TraceTextTests
{
    [Theory]
    [InlineData("/api/auth?user=admin&password=VM204", "/api/auth?user=…&password=…")]
    [InlineData("/api/status?KEY=abc", "/api/status?KEY=…")]
    [InlineData("/api/relay/on?key=abc&relay=1,3", "/api/relay/on?key=…&relay=1,3")]
    [InlineData("/api/status", "/api/status")]
    [InlineData("/api/status?key", "/api/status?key=…")]
    public void Should_HideTheKeyAndTheLogin(string target, string shown)
    {
        Assert.Equal(shown, Vm204TraceText.Redact(target));
    }

    [Theory]
    [InlineData("/api/auth?user=a&password=b", "Log in")]
    [InlineData("/api/status?key=k", "Ask for status")]
    [InlineData("/api/relay/on?key=k&relay=3", "Turn on relay 3")]
    [InlineData("/api/relay/off?key=k&relay=1,2,4", "Turn off relay 1, 2 and 4")]
    [InlineData("/api/relay/on?key=k&relay=1,2,3,4", "Turn on all")]
    [InlineData("/api/relay/on?key=k&relay=9,x,", "Turn on no relays")]
    [InlineData("/index.html", "Ask for /index.html")]
    public void Should_SayWhatARequestAsksFor(string target, string text)
    {
        Assert.Equal(text, Vm204TraceText.Request(target));
    }

    [Fact]
    public void Should_DescribeTheStatus()
    {
        Assert.Equal("Status: relay 1 and 3 on · input 2 on · analog 512", Vm204TraceText.Status(new Vm204Status(0b0101, 0b0010, 512)));
        Assert.Equal("Status: all off · inputs off", Vm204TraceText.Status(new Vm204Status(0, 0, null)));
        Assert.Equal("Status: all on · all inputs on · analog 0", Vm204TraceText.Status(new Vm204Status(0b1111, 0b1111, 0)));
    }

    [Fact]
    public void Should_ShowAnAnswer_WithoutItsKeyOrAnythingUnprintable()
    {
        Assert.Equal("200 {\"ApiKey\":\"…\"}", Vm204TraceText.Answer(200, "{\"ApiKey\":\"secretkey\"}", hideBody: true));
        Assert.Equal("400 bad·request", Vm204TraceText.Answer(400, "bad\nrequest"));
        Assert.Equal("404", Vm204TraceText.Answer(404, ""));
        Assert.Equal(205, Vm204TraceText.Answer(200, new string('x', 10_000)).Length);
    }

    [Fact]
    public void Should_WriteAndReadTheRelayParameter()
    {
        Assert.Equal("1,3", Vm204TraceText.RelayParameter(0b0101));
        Assert.Equal(0b0101, Vm204TraceText.RelayMask("key=k&relay=1,3"));
        Assert.Equal(0b1000, Vm204TraceText.RelayMask("RELAY=4,5,0,-1,04x"));
    }
}
