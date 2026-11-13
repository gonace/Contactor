using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The schedule screen and the Relay screen's schedule list, as the server first renders them.
/// </summary>
public sealed class SchedulePageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_OfferWeekdaysForANewSchedule()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/2/schedule/new", Cancel));

        Assert.Matches("<h1[^>]*>New schedule</h1>", html);
        Assert.Contains("Relay 2", html, StringComparison.Ordinal);
        Assert.Contains("Monday to Friday: the relay turns on at 06:30 and off at 08:00.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Saturday\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete schedule", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowAnExistingScheduleWithDelete()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        var schedules = app.Services.GetRequiredService<SchedulesHandler>();
        await schedules.SaveAsync(new ScheduleModel(0, 3, ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0), true), "john@example.com", Cancel);
        var id = (await schedules.ListAsync(3, Cancel))[0].Id;

        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/schedule/{id}", Cancel));

        Assert.Matches("<h1[^>]*>Edit schedule</h1>", html);
        Assert.Contains("Saturday: the relay turns on at 22:00 and off at 01:00 the next day.", html, StringComparison.Ordinal);
        Assert.Contains("Delete schedule", html, StringComparison.Ordinal);
        Assert.Contains("href=\"relay/3\"", html, StringComparison.Ordinal);

        var relay = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));
        Assert.Contains("Saturday", relay, StringComparison.Ordinal);
        Assert.Contains("22:00 – 01:00", relay, StringComparison.Ordinal);
        Assert.Contains($"href=\"schedule/{id}\"", relay, StringComparison.Ordinal);
        Assert.Matches("aria-pressed=\"true\"[^>]*>Active</button>", relay);
    }

    [Theory]
    [InlineData("/schedule/999", "The schedule no longer exists.")]
    [InlineData("/relay/9/schedule/new", "The relay doesn't exist.")]
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
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
    }
}
