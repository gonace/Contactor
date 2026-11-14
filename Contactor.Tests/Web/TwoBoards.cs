using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The app with its first board (8 relays, 1 input) and Barn (4 relays, 2 inputs, its relay 1 named "Barn light"), both
/// in memory, signed in as John.
/// </summary>
internal sealed class TwoBoards : IAsyncDisposable
{
    public const string John = "john@example.com";

    private TwoBoards(TestWebApp app, FakeRelayBoard first, FakeRelayBoard barn, int barnId)
    {
        App = app;
        First = first;
        Barn = barn;
        BarnId = barnId;
    }

    public TestWebApp App { get; }

    public FakeRelayBoard First { get; }

    public FakeRelayBoard Barn { get; }

    public int BarnId { get; }

    public IDbContextFactory<ContactorDbContext> Factory => App.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();

    public static async Task<TwoBoards> StartAsync(FakeRelayBoard? barn = null)
    {
        var first = new FakeRelayBoard();
        barn ??= new FakeRelayBoard(relayCount: 4, inputCount: 2);
        var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(first));
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        int barnId;
        await using (var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var row = new Board { Name = "Barn", SortOrder = 2 };
            db.Boards.Add(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            barnId = row.Id;
        }

        await app.Services.SeedBoardAsync(barnId, barn.Capabilities.RelayCount, barn.Capabilities.InputCount, TestContext.Current.CancellationToken);
        await using (var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await db.Relays.Where(r => r.BoardId == barnId && r.Channel == 1).ExecuteUpdateAsync(r => r.SetProperty(x => x.Name, "Barn light"), TestContext.Current.CancellationToken);
        }

        await app.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(barn).Build(barnId), TestContext.Current.CancellationToken);
        return new TwoBoards(app, first, barn, barnId);
    }

    public ValueTask DisposeAsync()
    {
        return App.DisposeAsync();
    }
}
