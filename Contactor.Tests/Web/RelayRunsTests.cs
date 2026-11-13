using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Tests.Web;

/// <summary>
/// A relay's run is written only through <see cref="RelayRuns"/>, and a save never overwrites a change someone else
/// saved after the row was read: each wish is applied again to what is there, by its own rule.
/// </summary>
public sealed class RelayRunsTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private const string John = "john@example.com";
    private static readonly DateTime T = new(2026, 11, 4, 13, 0, 0, DateTimeKind.Utc);

    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create();
        await _database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_RaiseTheVersion_AtEverySave()
    {
        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            await RelayRuns.StartAsync(db, 1, Jane, T, T.AddHours(2), Cancel);
            await RelayRuns.SaveAsync(db, Cancel);
        }

        await using (var db = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            await RelayRuns.EndAsync(db, 1, T.AddMinutes(1), Cancel);
            await RelayRuns.SaveAsync(db, Cancel);
        }

        Assert.Equal(2, (await RunAsync(1)).Version);
    }

    // The database enforces it, whoever writes: a save from a row changed since it was read fails, and writes nothing.
    [Fact]
    public async Task Should_RefuseAStaleSave_ThatDoesNotGoThroughRelayRuns()
    {
        await using var stale = await _database.Factory.CreateDbContextAsync(Cancel);
        var run = await stale.RelayRuntimes.SingleAsync(r => r.RelayId == 1, Cancel);
        await SaveAsync(db => RelayRuns.StartAsync(db, 1, Jane, T, null, Cancel));

        run.TurnedOnBy = John;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Cancel));

        Assert.Equal(Jane, (await RunAsync(1)).TurnedOnBy);
    }

    // After an ON the one who turned it on owns the run, whatever was saved meanwhile; its event is saved with it.
    [Fact]
    public async Task Should_KeepAStart_OverAChangeSavedMeanwhile()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.StartAsync(db, 1, Jane, T, T.AddHours(2), Cancel);
        db.EventLog.Add(new EventLogEntry { TimestampUtc = T, RelayId = 1, Actor = Jane, Action = EventActions.On });
        await SaveAsync(other => RelayRuns.StartOutsideAsync(other, 1, T, T.AddHours(1), Cancel));

        await RelayRuns.SaveAsync(db, Cancel);

        var run = await RunAsync(1);
        Assert.Equal((Jane, (DateTime?)T, (DateTime?)T.AddHours(2)), (run.TurnedOnBy, run.OnSinceUtc, run.AutoOffAtUtc));
        Assert.Single(await EventsAsync());
    }

    // After an OFF only the run that was on when it went out ends: one begun afterwards stays.
    [Theory]
    [InlineData(-1, null)]
    [InlineData(1, John)]
    public async Task Should_EndOnlyTheRunThatWasOn_WhenTheOffWentOut(int startedMinutesAfterTheOff, string? expected)
    {
        await SaveAsync(db => RelayRuns.StartAsync(db, 1, Jane, T.AddMinutes(-10), null, Cancel));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.EndAsync(db, 1, T, Cancel);
        await SaveAsync(other => RelayRuns.StartAsync(other, 1, John, T.AddMinutes(startedMinutesAfterTheOff), null, Cancel));

        await RelayRuns.SaveAsync(db, Cancel);

        Assert.Equal(expected, (await RunAsync(1)).TurnedOnBy);
    }

    // Without a command, a change saved by someone else after the row was read is newer: it stays.
    [Fact]
    public async Task Should_LetANewerRunStand_OverAChangeSeenOutsideTheApp()
    {
        await using var outside = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.StartOutsideAsync(outside, 1, T, T.AddHours(2), Cancel);
        await using var off = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.EndOutsideAsync(off, 2, Cancel);
        await SaveAsync(db => RelayRuns.StartAsync(db, 1, Jane, T.AddSeconds(1), null, Cancel));
        await SaveAsync(db => RelayRuns.StartAsync(db, 2, John, T.AddSeconds(1), null, Cancel));

        await RelayRuns.SaveAsync(outside, Cancel);
        await RelayRuns.SaveAsync(off, Cancel);

        Assert.Equal(Jane, (await RunAsync(1)).TurnedOnBy);
        Assert.Equal(John, (await RunAsync(2)).TurnedOnBy);
    }

    [Fact]
    public async Task Should_GiveItsOwnAutoOff_OnlyWhileTheRelayHasNoRun()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.GiveOwnAutoOffAsync(db, 1, T, T.AddHours(2), Cancel);
        await SaveAsync(other => RelayRuns.StartAsync(other, 1, Jane, T, null, Cancel));

        await RelayRuns.SaveAsync(db, Cancel);
        await using (var now = await _database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.False(await RelayRuns.GiveOwnAutoOffNowAsync(now, 1, T, T.AddHours(2), Cancel));
            Assert.True(await RelayRuns.GiveOwnAutoOffNowAsync(now, 2, T, T.AddHours(2), Cancel));
        }

        Assert.Equal((Jane, (DateTime?)null), ((await RunAsync(1)).TurnedOnBy, (await RunAsync(1)).AutoOffAtUtc));
        Assert.Equal((EventActors.External, 1L), ((await RunAsync(2)).TurnedOnBy, (await RunAsync(2)).Version));
    }

    [Fact]
    public async Task Should_TakeOverForTheSchedule_OnlyARunOutsideTheApp()
    {
        await SaveAsync(db => RelayRuns.StartOutsideAsync(db, 1, T, T.AddHours(2), Cancel));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.TakeOverForScheduleAsync(db, 1, Cancel);
        await SaveAsync(other => RelayRuns.StartAsync(other, 1, Jane, T.AddSeconds(1), T.AddHours(1), Cancel));

        await RelayRuns.SaveAsync(db, Cancel);

        Assert.Equal((Jane, (DateTime?)T.AddHours(1)), ((await RunAsync(1)).TurnedOnBy, (await RunAsync(1)).AutoOffAtUtc));
    }

    [Fact]
    public async Task Should_MoveTheAutoOff_OnlyOfTheSameRun()
    {
        await SaveAsync(db => RelayRuns.StartAsync(db, 1, Jane, T, T.AddHours(2), Cancel));
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.SetAutoOffAsync(db, 1, T, T.AddHours(4), Cancel);
        await SaveAsync(other => RelayRuns.StartAsync(other, 1, John, T.AddMinutes(5), T.AddHours(1), Cancel));

        await RelayRuns.SaveAsync(db, Cancel);

        Assert.Equal((John, (DateTime?)T.AddHours(1)), ((await RunAsync(1)).TurnedOnBy, (await RunAsync(1)).AutoOffAtUtc));
    }

    // A relay that joins a group has no run of its own, whatever was saved meanwhile.
    [Fact]
    public async Task Should_Clear_OverAChangeSavedMeanwhile()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await RelayRuns.ClearAsync(db, 1, Cancel);
        await SaveAsync(other => RelayRuns.StartAsync(other, 1, Jane, T, T.AddHours(1), Cancel));

        await RelayRuns.SaveAsync(db, Cancel);

        Assert.Null((await RunAsync(1)).TurnedOnBy);
    }

    // Only RelayRuns sets a run's fields: a writer elsewhere would bypass its rules (the version still stops a stale
    // save, but not a fresh one with the wrong rule).
    [Fact]
    public void Should_WriteARunOnlyThroughRelayRuns()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web");
        var write = new System.Text.RegularExpressions.Regex(@"\.(OnSinceUtc|AutoOffAtUtc|TurnedOnBy)\s*=[^=>]|SetProperty\(\w+ => \w+\.(OnSinceUtc|AutoOffAtUtc|TurnedOnBy)");
        List<string> found = [];
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetFileName(f) != "RelayRuns.cs"))
        {
            var text = File.ReadAllText(file);

            // GroupRuntime has fields of the same names; only a file that touches relay runs counts.
            if (!text.Contains("RelayRuntime", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (write.IsMatch(line))
                {
                    found.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }

    private async Task SaveAsync(Func<ContactorDbContext, Task> wish)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        await wish(db);
        await RelayRuns.SaveAsync(db, Cancel);
    }

    private async Task<RelayRuntime> RunAsync(int relayId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    private async Task<List<EventLogEntry>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().ToListAsync(Cancel);
    }
}
