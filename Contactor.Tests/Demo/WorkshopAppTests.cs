using System.Text.Json;
using Contactor.Boards.Velleman;
using Contactor.Demo;
using Contactor.Tests.Web;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
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

        // The last event before the app starts, read from the file: the app's services would start the app, and its
        // board could log its first events before the read. The scheduler's last minute goes back one, so its first
        // tick after the connection, the one that reconciles at start, shows in the file when it is done; no schedule
        // starts or stops in that minute.
        int lastBefore;
        DateTime lastTickBefore;
        await using (var db = OpenBeforeStart(database.ConnectionString(dataFolder)))
        {
            lastBefore = await db.EventLog.MaxAsync(e => (int?)e.Id, Cancel) ?? 0;
            var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
            lastTickBefore = runtime.LastTickUtc!.Value.AddMinutes(-1);
            runtime.LastTickUtc = lastTickBefore;
            await db.SaveChangesAsync(Cancel);
        }

        // Relays a person left on at the board: the outdoor lights outside their schedule's window (relay 6) and a relay
        // with no schedule (relay 7). Nothing at start may turn them off.
        await simulator.SwitchRelayAsync(6, turnOn: true);
        await simulator.SwitchRelayAsync(7, turnOn: true);

        var client = app.CreateClient();
        await UntilAsync(() => ConnectedAsync(client));
        await UntilAsync(async () =>
        {
            await using var db = Open(app);
            var tick = await db.BoardRuntimes.Select(r => r.LastTickUtc).SingleAsync(Cancel);
            return tick > lastTickBefore;
        });

        // The start is handled: what it switched on is on at the board, and relays 6 and 7 are still on.
        var scheduled = on.Aggregate(0, (mask, channel) => mask | (1 << (channel - 1)));
        var expected = scheduled | 0b0110_0000;
        await UntilAsync(async () => on.Length == 0 || await LoggedAsync(app, EventActions.On, lastBefore));
        await UntilAsync(() => Task.FromResult((simulator.Outputs & scheduled) == scheduled));
        Assert.Equal(expected, simulator.Outputs);

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
            var since = await db.EventLog.Where(e => e.Id > lastBefore).ToListAsync(Cancel);
            Assert.DoesNotContain(since, e => e.Action is EventActions.InterlockTrip or EventActions.InputChanged or EventActions.Off or EventActions.GroupOff);
            Assert.Equal(on.Length, since.Count(e => e.Action == EventActions.On && e.Actor == EventActors.Schedule));
        }

        Assert.Equal(expected, simulator.Outputs);

        // The board's own timers are left as they are: they change only on a person's press.
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command is Vm201Command.TimerDisable or Vm201Command.TimerEnable or Vm201Command.TimerToggle);
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

    private static ContactorDbContext OpenBeforeStart(string connectionString)
    {
        // No pooled connection keeps the file open once the test ends.
        var unpooled = new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
        return new ContactorDbContext(new DbContextOptionsBuilder<ContactorDbContext>().UseSqlite(unpooled).Options);
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
