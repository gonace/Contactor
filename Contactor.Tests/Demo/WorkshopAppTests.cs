using System.Text.Json;
using Contactor.Boards.Vm201;
using Contactor.Demo;
using Contactor.Tests.Web;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var database = new AppCopyDatabase(time, Options.Create(new DemoOptions()));
        await database.PrepareAsync(dataFolder, Cancel);
        Assert.True(await database.ApplyTemplateAsync(dataFolder, Cancel));
        int eventsBefore;
        await using (var db = Open(app))
        {
            eventsBefore = await db.EventLog.CountAsync(Cancel);
        }

        var client = app.CreateClient();
        await Wait.UntilAsync(() => Connected(client));

        // The board's first answer is handled, and its own timers turned off, as on any start.
        await Wait.UntilAsync(() => Logged(app, EventActions.BoardTimersDisabled, eventsBefore));
        var expected = on.Aggregate(0, (mask, channel) => mask | (1 << (channel - 1)));
        await Wait.UntilAsync(() => simulator.Outputs == expected);
        await Wait.UntilAsync(() => on.Length == 0 || Logged(app, EventActions.On, eventsBefore));

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

    private static bool Connected(HttpClient client)
    {
        try
        {
            var json = client.GetStringAsync("/healthz", Cancel).GetAwaiter().GetResult();
            using var health = JsonDocument.Parse(json);
            return health.RootElement.GetProperty("boardConnected").GetBoolean();
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static bool Logged(TestWebApp app, string action, int afterId)
    {
        using var db = Open(app);
        return db.EventLog.Any(e => e.Id > afterId && e.Action == action);
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
