using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The schedule screen and the Relä screen's schedule list, as the server first renders them.
/// </summary>
public sealed class SchedulePageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_OfferWeekdaysForANewSchedule()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/rele/2/schema/ny", Cancel));

        Assert.Matches("<h1[^>]*>Nytt schema</h1>", html);
        Assert.Contains("Relä 2", html, StringComparison.Ordinal);
        Assert.Contains("Måndag till fredag: reläet slås på 06:30 och stängs av 08:00.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Lördag\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ta bort schema", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowAnExistingScheduleWithDelete()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        var schedules = app.Services.GetRequiredService<SchedulesHandler>();
        await schedules.SaveAsync(new ScheduleModel(0, 3, ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0), true), "ted@example.com", Cancel);
        var id = (await schedules.ListAsync(3, Cancel))[0].Id;

        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/schema/{id}", Cancel));

        Assert.Matches("<h1[^>]*>Ändra schema</h1>", html);
        Assert.Contains("Lördag: reläet slås på 22:00 och stängs av 01:00 dagen efter.", html, StringComparison.Ordinal);
        Assert.Contains("Ta bort schema", html, StringComparison.Ordinal);
        Assert.Contains("href=\"rele/3\"", html, StringComparison.Ordinal);

        var relay = WebUtility.HtmlDecode(await http.GetStringAsync("/rele/3", Cancel));
        Assert.Contains("Lördag", relay, StringComparison.Ordinal);
        Assert.Contains("22:00 – 01:00", relay, StringComparison.Ordinal);
        Assert.Contains($"href=\"schema/{id}\"", relay, StringComparison.Ordinal);
        Assert.Matches("aria-pressed=\"true\"[^>]*>Aktiv</button>", relay);
    }

    [Theory]
    [InlineData("/schema/999", "Schemat finns inte längre.")]
    [InlineData("/rele/9/schema/ny", "Reläet finns inte.")]
    public async Task Should_SaySoForAMissingScheduleOrRelay(string path, string message)
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));

        Assert.Contains(message, html, StringComparison.Ordinal);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
    }
}
