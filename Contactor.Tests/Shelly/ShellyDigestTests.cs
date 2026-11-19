using Contactor.Boards.Shelly;

namespace Contactor.Tests.Shelly;

/// <summary>Digest authentication with SHA-256: RFC 7616's own example and the docs' challenges.</summary>
public sealed class ShellyDigestTests
{
    [Fact]
    public void Should_GiveTheResponseOfRfc7616()
    {
        // RFC 7616, section 3.9.1, the SHA-256 response.
        var response = ShellyDigest.Response(
            "Mufasa",
            "http-auth@example.org",
            "Circle of Life",
            "GET",
            "/dir/index.html",
            "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v",
            "00000001",
            "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ");

        Assert.Equal("753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1", response);
    }

    [Fact]
    public void Should_HashTheDocsHa1AsTheirJavaScriptDoes()
    {
        // "Authentication", "HA1 calculation with JavaScript": sha256("admin:shellymodel-XXXXXXXXXXXX:mysecretpassword") as
        // hex. The docs print no result; this one is from Python's hashlib.
        Assert.Equal(
            "be901e0ff91cb0b96e7daf50eae36e433b999338235977343fd9d1f5a9ba9a1a",
            ShellyDigest.Hash("admin:shellymodel-XXXXXXXXXXXX:mysecretpassword"));
    }

    [Fact]
    public void Should_ReadTheDocsChallenges()
    {
        // "Authentication", "Authentication over HTTP" (2.0.0 and later, the nonce shortened as the docs print it) and
        // "Authentication Challenge Example (Legacy)".
        Assert.Equal(
            new ShellyChallenge("shellypro4pm-f008d1d8b8b8", "AAAAAABn...(base64)...", Stale: false),
            ShellyDigest.ParseChallenge(["Digest qop=\"auth\", realm=\"shellypro4pm-f008d1d8b8b8\", nonce=\"AAAAAABn...(base64)...\", algorithm=SHA-256"]));
        Assert.Equal(
            new ShellyChallenge("shellypro4pm-f008d1d8b8b8", "60dc59c6", Stale: false),
            ShellyDigest.ParseChallenge(["Digest qop=\"auth\", realm=\"shellypro4pm-f008d1d8b8b8\", nonce=\"60dc59c6\", algorithm=SHA-256"]));
        Assert.True(ShellyDigest.ParseChallenge(["Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256, stale=TRUE"])!.Stale);
    }

    [Fact]
    public void Should_TakeTheFirstChallengeItCanAnswer()
    {
        Assert.Equal(
            "n2",
            ShellyDigest.ParseChallenge(["Basic realm=\"x\"", "Digest qop=\"auth\", realm=\"r\", nonce=\"n1\", algorithm=MD5", "Digest qop=\"auth-int,auth\", realm=\"r\", nonce=\"n2\", algorithm=sha-256"])!.Nonce);
    }

    // One header may hold more than one challenge: the Digest's parameters end where the next challenge starts.
    [Theory]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256, Basic realm=\"x\"")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256,Basic realm=\"x\"")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256, Negotiate")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256, Bearer abc==")]
    public void Should_ReadTheDigestChallenge_WhenAnotherFollowsInTheSameHeader(string header)
    {
        Assert.Equal(new ShellyChallenge("r", "n", Stale: false), ShellyDigest.ParseChallenge([header]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Digest")]
    [InlineData("Digestqop=\"auth\"")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\"")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=MD5")]
    [InlineData("Digest qop=\"auth-int\", realm=\"r\", nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"a\\\"b\", nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"a\\\\b\", nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n\u0001\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm=\"r\", nonce=\"n, algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", realm, nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest qop=\"auth\", Basic realm=\"r\", nonce=\"n\", algorithm=SHA-256")]
    [InlineData("Digest Basic realm=\"r\"")]
    public void Should_RefuseAChallengeItCantAnswer(string header)
    {
        Assert.Null(ShellyDigest.ParseChallenge([header]));
    }

    [Fact]
    public void Should_RefuseHugeHeaders()
    {
        var nonce = new string('n', 300);
        Assert.Null(ShellyDigest.ParseChallenge([$"Digest qop=\"auth\", realm=\"r\", nonce=\"{nonce}\", algorithm=SHA-256"]));
        Assert.Null(ShellyDigest.ParseChallenge(["Digest qop=\"auth\", realm=\"r\", nonce=\"n\", algorithm=SHA-256, x=\"" + new string('x', 3000) + "\""]));
    }

    [Fact]
    public void Should_WriteAnAuthorizationTheDeviceCanCheck()
    {
        var challenge = new ShellyChallenge("shellypro4pm-f008d1d8b8b8", "60dc59c6", Stale: false);

        var header = ShellyDigest.Authorization(challenge, "mysecretpassword", 10, "313273957");
        var credentials = ShellyDigest.ParseAuthorization(header)!;

        Assert.Equal(("admin", "shellypro4pm-f008d1d8b8b8", "60dc59c6", "/rpc", "SHA-256", "auth", "0000000a", "313273957"), (credentials.Username, credentials.Realm, credentials.Nonce, credentials.Uri, credentials.Algorithm, credentials.Qop, credentials.NonceCount, credentials.ClientNonce));
        Assert.Equal(
            ShellyDigest.Response("admin", "shellypro4pm-f008d1d8b8b8", "mysecretpassword", "POST", "/rpc", "60dc59c6", "0000000a", "313273957"),
            credentials.Response);
        Assert.DoesNotContain("mysecretpassword", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CountNoncesAsEightHexDigits()
    {
        Assert.Equal("00000001", ShellyDigest.NonceCount(1));
        Assert.Equal("0000000a", ShellyDigest.NonceCount(10));
        Assert.Equal("00007530", ShellyDigest.NonceCount(30_000));
    }

    [Fact]
    public void Should_HashAnyPasswordAsUtf8()
    {
        // The device's own web page hashes with JavaScript's crypto, which takes the string as UTF-8.
        // The value is from Python's hashlib, over the UTF-8 bytes.
        Assert.Equal("f0d7d0bc565ec2b5b136aa2ef7612bf2bc8ee1c94a048686771b033e7a0dabca", ShellyDigest.Hash("admin:r:lösen"));
    }

    [Fact]
    public void Should_NeverSendAValueThatBreaksTheHeader()
    {
        Assert.Throws<ArgumentException>(() => ShellyDigest.Authorization(new ShellyChallenge("r\"x", "n", false), "p", 1, "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyDigest.Authorization(new ShellyChallenge("r", "n", false), "p", 0, "1"));
    }
}
