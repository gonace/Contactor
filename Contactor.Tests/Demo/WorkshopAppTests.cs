using System.Text.Json;
using Contactor.Boards.Vm201;
using Contactor.Demo;
using Contactor.Tests.Web;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Demo;

/// <summary>
/// The whole app started on the Workshop template, as a visitor's copy is after "Skip setup", against a simulated VM201:
/// it opens on the relays with the board connected, and switches nothing but what its rules say at start.
/// </summary>
public sealed class WorkshopAppTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(12, 0, new int[0])] // No schedule runs: nothing is switched.
    [InlineData(7, 30, new[] { 5 })] // The ventilation's window runs: the scheduler turns it on, as at any start.
    public async Task Should_StartOnTheTemplate_WithTheBoardConnected_AndSwitchOnlyWhatARunningScheduleSays(int hour, int minute, int[] on)
    {
        // A Wednesday in Stockholm, with the clock running.
        var local = new DateTime(2026, 11, 11, hour, minute, 0, DateTimeKind.Unspecified);
        var time = new ShiftedTime(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Stockholm)) - DateTimeOffset.UtcNow);
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TimeZone"] = "Europe/Stockholm",
            },
            services => services.AddSingleton<TimeProvider>(time),
            setUp: false);
        var dataFolder = Path.GetDirectoryName(app.DatabasePath)!;
        Directory.CreateDirectory(dataFolder);
        var database = new AppCopyDatabase(time);
        await database.PrepareAsync(dataFolder, Cancel);
        Assert.True(await database.ApplyTemplateAsync(dataFolder, Stockholm, Cancel));
        int eventsBefore;
        await using (var db = Open(app))
        {
            eventsBefore = await db.EventLog.CountAsync(Cancel);
        }

        var client = app.CreateClient();
        await UntilAsync(() => ConnectedAsync(client));

        // The board's first answer is handled, and its own timers turned off, as on any start.
        await UntilAsync(() => LoggedAsync(app, EventActions.BoardTimersDisabled, eventsBefore));
        var expected = on.Aggregate(0, (mask, channel) => mask | (1 << (channel - 1)));
        await UntilAsync(() => Task.FromResult(simulator.Outputs == expected));
        await UntilAsync(async () => on.Length == 0 || await LoggedAsync(app, EventActions.On, eventsBefore));

        var page = await client.GetStringAsync("/", Cancel);
        Assert.Contains("Fan heater", page, StringComparison.Ordinal);
        Assert.Contains("Air compressor", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Which relay board?", page, StringComparison.Ordinal);
        Assert.DoesNotContain("About you", page, StringComparison.Ordinal);
        var events = await client.GetStringAsync("/events", Cancel);
        Assert.Contains("Alex", events, StringComparison.Ordinal);
        Assert.Contains("Sam", events, StringComparison.Ordinal);

        // Since the start: no safety shut-off, no door noticed after a gap, and nothing turned off.
        await using (var db = Open(app))
        {
            var since = await db.EventLog.Where(e => e.Id > eventsBefore).ToListAsync(Cancel);
            Assert.DoesNotContain(since, e => e.Action is EventActions.InterlockTrip or EventActions.InputChanged or EventActions.Off or EventActions.GroupOff);
            Assert.Equal(on.Length, since.Count(e => e.Action == EventActions.On && e.Actor == EventActors.Schedule));
        }

        Assert.Equal(expected, simulator.Outputs);
    }

    // The whole app starts here, with the rest of the tests running beside it: a longer deadline than Wait's, and no
    // thread blocked while it waits.
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met.");
            }

            await Task.Delay(50, Cancel);
        }
    }

    private static async Task<bool> ConnectedAsync(HttpClient client)
    {
        try
        {
            var json = await client.GetStringAsync("/healthz", Cancel);
            using var health = JsonDocument.Parse(json);
            return health.RootElement.GetProperty("boardConnected").GetBoolean();
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<bool> LoggedAsync(TestWebApp app, string action, int afterId)
    {
        await using var db = Open(app);
        return await db.EventLog.AnyAsync(e => e.Id > afterId && e.Action == action, Cancel);
    }

    private static ContactorDbContext Open(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContext();
    }

    // The real clock, moved to the test's day: timers and delays run as usual.
    private sealed class ShiftedTime : TimeProvider
    {
        private readonly TimeSpan _shift;

        public ShiftedTime(TimeSpan shift)
        {
            _shift = shift;
        }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _shift;
    }
}
