using System.Net;
using Contactor.Web.Components;

namespace Contactor.Tests.Web;

public sealed class SettingsPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowTheBoardModelsLoginHint()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Vm201:Username"] = "admin",
                ["Vm201:Password"] = "secret",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("id=\"username\"[^>]*value=\"admin\"", html);
        Assert.Contains("At most 8 characters: letters a–z, digits and plain symbols.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AskForTheCommunityAsThePassword_AndNeverShowIt_OnADaenetIp2Kit()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Board:Type"] = "denkovi-daenetip2-v2-16",
                ["DaenetIp2:Host"] = "127.0.0.1",
                ["DaenetIp2:Port"] = "1",
                ["DaenetIp2:Community"] = "quince7",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Denkovi DAEnetIP2 v2 16-relay kit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"username\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"no-login\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The board has no password", html, StringComparison.Ordinal);
        Assert.Matches("id=\"password\"[^>]*placeholder=\"unchanged\"", html);
        Assert.Contains("On a new board it is private.", html, StringComparison.Ordinal);
        Assert.Contains("With a wrong community the board doesn't answer: check the address and the community.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("quince7", html, StringComparison.Ordinal);
    }

    // Before any community is saved the field is still there, with the hint: the board always has one.
    [Fact]
    public async Task Should_AskForTheCommunity_WhenNoneIsSaved_OnADaenetIp2Kit()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = "john@example.com",
                ["Board:Type"] = "denkovi-daenetip2-v2-8",
                ["DaenetIp2:Host"] = "127.0.0.1",
                ["DaenetIp2:Port"] = "1",
            });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.DoesNotContain("id=\"no-login\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("On a new board it is private.", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(68 * 1024, "68 kB")]
    [InlineData(100, "1 kB")]
    [InlineData(1363149, "1.3 MB")]
    public void Should_GiveABackupsSize(long bytes, string size)
    {
        Assert.Equal(size, Settings.Size(TestTexts.Localizer, bytes));
    }
}
