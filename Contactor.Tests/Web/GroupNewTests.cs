using System.Net;
using Contactor.Web.Features.Groups;

namespace Contactor.Tests.Web;

/// <summary>"New group": what it offers and the plain-language plan it shows before creating.</summary>
public sealed class GroupNewTests
{
    private static readonly GroupMemberDraft[] FanHeater =
    [
        new(3, "Fan", 3, 300),
        new(1, "Heat", 10, 0),
        new(5, "Lamp", 1, 0),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_DescribeTheStartFromTheTop_WithTheWaits()
    {
        Assert.Equal("Fan → wait 10 s → Heat → wait 1 s → Lamp", GroupPlanText.Start(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheStopFromTheBottom_WithTheRunOn()
    {
        Assert.Equal("Lamp off → Heat off → Fan runs on for 5 min, then off", GroupPlanText.Stop(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheInterlockForEachPair()
    {
        Assert.Equal(
            "Heat can only be on while Fan is on. Lamp can only be on while Heat is on.",
            GroupPlanText.Interlock(FanHeater));
    }

    [Theory]
    [InlineData(null, false, true, "Free", true)]
    [InlineData(null, false, false, "Inactive · free", true)]
    [InlineData("Painting", false, true, "Already in Painting", false)]
    [InlineData(null, true, true, "Has its own schedules", false)]
    public void Should_SayWhetherARelayCanJoin(string? group, bool hasSchedules, bool isActive, string note, bool isFree)
    {
        var relay = new GroupCandidate(4, "Relay 4", isActive, group, hasSchedules);

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
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/group/new", Cancel));

        Assert.Contains("href=\"group/new\"", list, StringComparison.Ordinal);
        Assert.Contains("STEP 1 OF 4", html, StringComparison.Ordinal);
        Assert.Contains("Name and icon", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Contains("Tools", html, StringComparison.Ordinal);
    }
}
