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

    // atoi, as parsePulseAPICommand reads time (Main/api.c): leading digits with a sign, 0 for none, the last one wins.
    [Theory]
    [InlineData("key=k&relay=1&time=2", 2)]
    [InlineData("time=2&relay=1&TIME=7", 7)]
    [InlineData("time=12s", 12)]
    [InlineData("time=-3", -3)]
    [InlineData("time=default", 0)]
    [InlineData("time=", 0)]
    [InlineData("time=99999999999", null)]
    [InlineData("key=k&relay=1", null)]
    public void Should_ReadThePulseTime_AsTheFirmwareDoes(string query, int? seconds)
    {
        Assert.Equal(seconds, Vm204TraceText.PulseSeconds(query));
    }

    [Fact]
    public void Should_SayWhatAPulseDoes()
    {
        Assert.Equal("Pulse relay 2 and 3 for 2 s", Vm204TraceText.Request("/api/relay/pulse?key=k&relay=2,3&time=2"));
        Assert.Equal("Pulse relay 1 for ? s", Vm204TraceText.Request("/api/relay/pulse?key=k&relay=1"));
    }
}
