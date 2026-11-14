using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>How a test database numbers the board's relays and inputs.</summary>
public enum RelayLayout
{
    /// <summary>As a new database seeds them: relay 1 on channel 1 and so on.</summary>
    Same,

    /// <summary>
    /// Ids that are not their channels: from 100 up, in reverse channel order (relay 108 on channel 1 of 8), so every
    /// place that takes an id for a channel either switches the wrong relay or finds no channel at all.
    /// </summary>
    Apart,
}

/// <summary>Builds the board's rows in a <see cref="RelayLayout"/>, before the app seeds the rest.</summary>
internal static class RelayLayouts
{
    public const int RelayBase = 100;

    public const int InputBase = 200;

    /// <summary>The relay's id on this channel, with <paramref name="relayCount"/> relays on the board.</summary>
    public static int RelayId(RelayLayout layout, int channel, int relayCount)
    {
        return layout == RelayLayout.Same ? channel : RelayBase + relayCount + 1 - channel;
    }

    /// <summary>The input's id on this channel, with <paramref name="inputCount"/> inputs on the board.</summary>
    public static int InputId(RelayLayout layout, int channel, int inputCount)
    {
        return layout == RelayLayout.Same ? channel : InputBase + inputCount + 1 - channel;
    }

    /// <summary>
    /// For <see cref="RelayLayout.Apart"/>: adds the board, and its relays and inputs with ids apart from their
    /// channels, as a database brought up to date from rows made elsewhere could have them. Seeding then finds every
    /// channel present and adds nothing. <see cref="RelayLayout.Same"/> adds nothing: the app seeds it.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, RelayLayout layout, int relayCount, int inputCount)
    {
        if (layout == RelayLayout.Same)
        {
            return;
        }

        var factory = services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db, layout, relayCount, inputCount);
    }

    /// <summary>As above, on a context of the caller's, e.g. on a database made before the app starts.</summary>
    public static async Task SeedAsync(ContactorDbContext db, RelayLayout layout, int relayCount, int inputCount)
    {
        if (layout == RelayLayout.Same)
        {
            return;
        }

        var board = await db.FirstBoardAsync(TestContext.Current.CancellationToken);
        if (board is null)
        {
            board = new Board { SortOrder = 1 };
            db.Boards.Add(board);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        for (var channel = 1; channel <= relayCount; channel++)
        {
            var id = RelayId(layout, channel, relayCount);
            db.Relays.Add(new Relay
            {
                Id = id,
                BoardId = board.Id,
                Channel = channel,
                Name = Relay.DefaultName(channel),
                IsActive = true,
                MaxOnMinutes = Relay.DefaultMaxOnMinutes,
                SortOrder = channel,
            });
            db.RelayRuntimes.Add(new RelayRuntime { RelayId = id });
        }

        for (var channel = 1; channel <= inputCount; channel++)
        {
            db.Inputs.Add(new Input { Id = InputId(layout, channel, inputCount), BoardId = board.Id, Channel = channel, Name = Input.DefaultName(channel) });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
