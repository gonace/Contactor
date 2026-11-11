using System.Net;
using Contactor.Web.Features.Groups;

namespace Contactor.Tests.Web;

/// <summary>"Ny grupp": what it offers and the plain-language plan it shows before creating.</summary>
public sealed class GroupNewTests
{
    private static readonly GroupMemberDraft[] FanHeater =
    [
        new(3, "Fläkt", 3, 300),
        new(1, "Värme", 10, 0),
        new(5, "Lampa", 1, 0),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_DescribeTheStartFromTheTop_WithTheWaits()
    {
        Assert.Equal("Fläkt → vänta 10 s → Värme → vänta 1 s → Lampa", GroupPlanText.Start(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheStopFromTheBottom_WithTheRunOn()
    {
        Assert.Equal("Lampa av → Värme av → Fläkt går 5 min till, sedan av", GroupPlanText.Stop(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheInterlockForEachPair()
    {
        Assert.Equal(
            "Värme kan bara vara på när Fläkt är på. Lampa kan bara vara på när Värme är på.",
            GroupPlanText.Interlock(FanHeater));
    }

    [Theory]
    [InlineData(null, false, true, "Ledigt", true)]
    [InlineData(null, false, false, "Inaktivt · ledigt", true)]
    [InlineData("Lackering", false, true, "Ingår redan i Lackering", false)]
    [InlineData(null, true, true, "Har egna scheman", false)]
    public void Should_SayWhetherARelayCanJoin(string? group, bool hasSchedules, bool isActive, string note, bool isFree)
    {
        var relay = new GroupCandidate(4, "Relä 4", isActive, group, hasSchedules);

        Assert.Equal((note, isFree), (relay.Note, relay.IsFree));
    }

    [Fact]
    public async Task Should_StartWithNameAndIcon()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/grupp/ny", Cancel));

        Assert.Contains("href=\"grupp/ny\"", list, StringComparison.Ordinal);
        Assert.Contains("STEG 1 AV 4", html, StringComparison.Ordinal);
        Assert.Contains("Namn och ikon", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Contains("Verktyg", html, StringComparison.Ordinal);
    }
}
