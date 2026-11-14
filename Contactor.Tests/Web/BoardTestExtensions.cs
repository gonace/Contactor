using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>Choosing the board in a test, as the setup guide does, and reaching the board and what only it has.</summary>
internal static class BoardTestExtensions
{
    /// <summary>Switches to the model with this id and adds its rows; returns the board now in use.</summary>
    public static async Task<ActiveBoard> UseBoardAsync(this IServiceProvider services, string type = "vm201")
    {
        var model = BoardCatalogue.Find(type)?.Model ?? throw new ArgumentException($"No board \"{type}\".", nameof(type));
        await services.GetRequiredService<BoardChoice>().UseAsync(model, TestContext.Current.CancellationToken);
        return services.Live().Active!;
    }

    /// <summary>
    /// Runs <paramref name="board"/> as the app's one board, as the setup guide would a real one: adds its rows (the
    /// board's own, its relays' and inputs') where there are none yet, unless <paramref name="seed"/> is false, and puts
    /// it on the first board's row.
    /// </summary>
    public static async Task<FakeBoardParts> UseFakeBoardAsync(this IServiceProvider services, IRelayBoard board, bool seed = true)
    {
        var cancel = TestContext.Current.CancellationToken;
        if (seed)
        {
            await services.SeedBoardAsync(board.Capabilities.RelayCount, board.Capabilities.InputCount, cancel);
        }

        int boardId;
        await using (var db = await services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(cancel))
        {
            boardId = await db.FirstBoardIdAsync(cancel) ?? throw new InvalidOperationException("No board's row.");
        }

        var parts = new FakeBoardParts(board);
        await services.GetRequiredService<BoardRegistry>().ReplaceAsync(parts.Build(boardId), cancel);
        return parts;
    }

    /// <summary>The id of the first board's row; throws when there is none.</summary>
    public static async Task<int> FirstBoardIdAsync(this IServiceProvider services)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.FirstBoardIdAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("No board's row.");
    }

    /// <summary>The app's one board.</summary>
    public static LiveBoard Live(this IServiceProvider services)
    {
        return Assert.Single(services.GetRequiredService<BoardRegistry>().All);
    }

    /// <summary>A service of the board in use, e.g. its client.</summary>
    public static T BoardService<T>(this IServiceProvider services)
        where T : notnull
    {
        var board = services.Live().Active ?? throw new InvalidOperationException("No board is in use.");
        return board.Services.GetRequiredService<T>();
    }
}
