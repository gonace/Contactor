using Contactor.Boards.Velleman;

namespace Contactor.Tests.Velleman;

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
