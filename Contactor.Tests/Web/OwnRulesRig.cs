using System.Text.Json;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The board's own rules with two boards in memory that read and turn off their rules: <see cref="Board"/>, the first,
/// which the tests watch, and <see cref="Other"/>, which they also use to know when the service has handled what
/// <see cref="Board"/> reported before (<see cref="HandledAsync"/>): the service handles the boards' states in order.
/// </summary>
internal sealed class OwnRulesRig : IAsyncDisposable
{
    public const string Erik = "erik@example.com";

    private OwnRulesRig(TestDatabase database, ManualTimeProvider time, FakeRelayBoard board, int boardId, FakeRelayBoard other, int otherId)
    {
        Time = time;
        Database = database;
        Board = board;
        BoardId = boardId;
        Other = other;
        OtherId = otherId;
    }

    public TestDatabase Database { get; }

    /// <summary>The app's clock, which only the test moves.</summary>
    public ManualTimeProvider Time { get; }

    public FakeRelayBoard Board { get; }

    public int BoardId { get; }

    public FakeRelayBoard Other { get; }

    public int OtherId { get; }

    public OwnRulesService Service => Database.Services.GetRequiredService<OwnRulesService>();

    public OwnRulesHandler Handler => Database.Services.GetRequiredService<OwnRulesHandler>();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Starts the rig; <paramref name="configure"/> may add to the app's services, e.g. a database interceptor.</summary>
    public static async Task<OwnRulesRig> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddSingleton<TimeProvider>(time);
            configure?.Invoke(services);
            services.AddLocalClock(configuration);
            services.AddBoards(configuration);
            services.AddRelays(configuration);
            services.AddOwnRules(configuration);
        });
        try
        {
            await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
            var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
            await database.Services.UseFakeBoardAsync(board);
            var boardId = await database.Services.FirstBoardIdAsync();

            var other = new FakeRelayBoard(relayCount: 4, ownRules: OwnRulesAbility.ReadsAndTurnsOff);
            int otherId;
            await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
            {
                var row = new Board { Name = "Barn", SortOrder = 2 };
                db.Boards.Add(row);
                await db.SaveChangesAsync(Cancel);
                otherId = row.Id;
            }

            await database.Services.SeedBoardAsync(otherId, other.Capabilities, Cancel);
            await database.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(other).Build(otherId), Cancel);
            await database.Services.GetRequiredService<OwnRulesService>().StartAsync(Cancel);
            var rig = new OwnRulesRig(database, time, board, boardId, other, otherId);

            // What the service read at its start is handled first: from here on each change of Other's comes after.
            await rig.HandledAsync();
            return rig;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns once the service has handled every state <see cref="Board"/> reported before: a change of
    /// <see cref="Other"/>'s rules, which the service handles after them, is seen in its rows.
    /// </summary>
    public async Task HandledAsync()
    {
        var had = Service.Rows(OtherId).Count > 0;
        Other.SetOwnRules(had ? OwnRules.Empty : new OwnRules([new OwnRule(1, OwnRuleKind.Timer)]));
        await Wait.UntilAsync(() => Service.Rows(OtherId).Count > 0 != had);
    }

    /// <summary>The board's rows in the database, in channel order.</summary>
    public async Task<List<BoardOwnRule>> RowsAsync(int? boardId = null)
    {
        var id = boardId ?? BoardId;
        await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
        return await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == id).OrderBy(r => r.Channel).ThenBy(r => r.Kind).ToListAsync(Cancel);
    }

    /// <summary>The events about <see cref="Board"/> with this action, oldest first.</summary>
    public async Task<List<EventLogEntry>> EventsAsync(string action, int? boardId = null)
    {
        var id = boardId ?? BoardId;
        await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().Where(e => e.BoardId == id && e.Action == action).OrderBy(e => e.Id).ToListAsync(Cancel);
    }

    /// <summary>The relay's id on <see cref="Board"/>.</summary>
    public async Task<int> RelayIdAsync(int channel)
    {
        await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == BoardId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }

    /// <summary>Whether a "found" event says the rule came while the app watched.</summary>
    public static bool IsNew(EventLogEntry entry)
    {
        using var details = JsonDocument.Parse(entry.Details!);
        return details.RootElement.GetProperty("new").GetBoolean();
    }

    public async ValueTask DisposeAsync()
    {
        await Service.StopAsync(CancellationToken.None);
        await Database.DisposeAsync();
    }
}
