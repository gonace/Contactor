using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>Choosing the board in a test, as the setup guide does, and reaching what only that board has.</summary>
internal static class BoardTestExtensions
{
    /// <summary>Switches to the model with this id and adds its rows; returns the board now in use.</summary>
    public static async Task<ActiveBoard> UseBoardAsync(this IServiceProvider services, string type = "vm201")
    {
        var model = BoardCatalogue.Find(type)?.Model ?? throw new ArgumentException($"No board \"{type}\".", nameof(type));
        await services.GetRequiredService<BoardChoice>().UseAsync(model, TestContext.Current.CancellationToken);
        return services.GetRequiredService<BoardSwitch>().Current!;
    }

    /// <summary>A service of the board in use, e.g. its client.</summary>
    public static T BoardService<T>(this IServiceProvider services)
        where T : notnull
    {
        var board = services.GetRequiredService<BoardSwitch>().Current ?? throw new InvalidOperationException("No board is in use.");
        return board.Services.GetRequiredService<T>();
    }
}
