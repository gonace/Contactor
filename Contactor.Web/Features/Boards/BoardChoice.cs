using Contactor.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Which model of board the app runs, while it runs: the one saved in the setup guide (<see cref="Board.Type"/> of the
/// board), or none until one is. Choosing one builds it, switches to it (<see cref="BoardSwitch"/>) and adds the rows for its
/// relays and inputs, as at start.
/// </summary>
public sealed partial class BoardChoice : IDisposable
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardSwitch _switch;
    private readonly IServiceProvider _app;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BoardChoice> _logger;

    // One choice at a time, e.g. two pages of the setup guide.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardChoice(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardSwitch boardSwitch,
        IServiceProvider app,
        IConfiguration configuration,
        ILogger<BoardChoice> logger)
    {
        _dbFactory = dbFactory;
        _switch = boardSwitch;
        _app = app;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// At start: runs the saved model, if any. An installation in use from before the setup guide chose the model (the
    /// guide is done, no model saved) gets the one <c>Board:Type</c> names, or the VM201 it ran before, saved once so
    /// it keeps it. Throws for a saved model the catalogue doesn't have. A board without a name gets its model's.
    /// </summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        BoardModel? model;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var board = await db.FirstBoardAsync(cancellationToken);
            if (board is not null && board.Type.Length > 0)
            {
                var saved = board.Type;
                model = BoardCatalogue.Find(saved)?.Model
                    ?? throw new InvalidOperationException(
                        $"The relay board chosen in the setup guide is \"{saved}\", which isn't a board this version of the app knows. Use one of: {BoardCatalogue.Ids}.");
            }
            else if (await db.Installations.AnyAsync(i => i.SetupDoneUtc != null, cancellationToken))
            {
                var type = _configuration.GetSection(BoardOptions.SectionName).Get<BoardOptions>()?.Type ?? BoardCatalogue.EarlierDefaultType;
                model = BoardCatalogue.Find(type)?.Model
                    ?? throw new InvalidOperationException(
                        $"Board:Type is \"{type}\", which isn't a board the app knows. Use one of: {BoardCatalogue.Ids}.");
                board ??= AddBoard(db);
                board.Type = model.Id;
                LogEarlierModelSaved(model.Name);
            }
            else
            {
                return;
            }

            if (board.Name.Length == 0)
            {
                board.Name = Board.DefaultName(model.Name);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await UseAsync(model, cancellationToken);
    }

    /// <summary>
    /// The setup guide's choice: runs <paramref name="model"/> from now on, as <see cref="UseAsync"/>, then saves it, so
    /// the next start runs it too. Saved only once it runs, so a board the app can't build (e.g. its settings are
    /// wrong) is never saved for the next start to fail on; one choice at a time, so the board saved is the one that
    /// runs.
    /// </summary>
    public Task ChooseAsync(BoardModel model, CancellationToken cancellationToken)
    {
        return UseAsync(model, save: true, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="model"/> from now on, unless it already is the board, and adds the rows for its relays and
    /// inputs. Doesn't save the choice (<see cref="ChooseAsync"/> does).
    /// </summary>
    public Task UseAsync(BoardModel model, CancellationToken cancellationToken)
    {
        return UseAsync(model, save: false, cancellationToken);
    }

    private async Task UseAsync(BoardModel model, bool save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var registration = BoardCatalogue.Find(model.Id)
            ?? throw new ArgumentException($"\"{model.Id}\" isn't a board in the catalogue.", nameof(model));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Rows first: from the switch on, the screens and services count on a row for every relay.
            await _app.SeedBoardAsync(model.Capabilities.RelayCount, model.Capabilities.InputCount, cancellationToken);
            if (_switch.Model?.Id != model.Id)
            {
                await _switch.SwitchToAsync(registration.Create(registration.Model, _app, _configuration));
            }

            if (save)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                var board = await db.FirstBoardAsync(cancellationToken) ?? AddBoard(db);

                // The name follows the model until someone gives it one of their own.
                if (board.Name.Length == 0 || BoardCatalogue.Find(board.Type) is { } earlier && board.Name == Board.DefaultName(earlier.Model.Name))
                {
                    board.Name = Board.DefaultName(model.Name);
                }

                board.Type = model.Id;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // At start, before the board's rows are seeded; its runtime row comes with them.
    private static Board AddBoard(ContactorDbContext db)
    {
        var board = new Board { SortOrder = 1 };
        db.Boards.Add(board);
        return board;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This installation was in use before the setup guide chose the relay board: it keeps the {Model}")]
    private partial void LogEarlierModelSaved(string model);
}
