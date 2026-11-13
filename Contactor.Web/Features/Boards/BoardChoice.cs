using Contactor.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Which model of board the app runs, while it runs: the one saved in the setup guide (<see cref="Installation.BoardType"/>),
/// or none until one is. Choosing one builds it, switches to it (<see cref="BoardSwitch"/>) and adds the rows for its
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
    /// it keeps it. Throws for a saved model the catalogue doesn't have.
    /// </summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        BoardModel? model;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var installation = await db.Installations.SingleOrDefaultAsync(cancellationToken);
            if (installation is null)
            {
                return;
            }

            if (installation.BoardType is { } saved)
            {
                model = BoardCatalogue.Find(saved)?.Model
                    ?? throw new InvalidOperationException(
                        $"The relay board chosen in the setup guide is \"{saved}\", which isn't a board this version of the app knows. Use one of: {BoardCatalogue.Ids}.");
            }
            else if (installation.SetupDoneUtc is not null)
            {
                var type = _configuration.GetSection(BoardOptions.SectionName).Get<BoardOptions>()?.Type ?? BoardCatalogue.EarlierDefaultType;
                model = BoardCatalogue.Find(type)?.Model
                    ?? throw new InvalidOperationException(
                        $"Board:Type is \"{type}\", which isn't a board the app knows. Use one of: {BoardCatalogue.Ids}.");
                installation.BoardType = model.Id;
                await db.SaveChangesAsync(cancellationToken);
                LogEarlierModelSaved(model.Name);
            }
            else
            {
                return;
            }
        }

        await UseAsync(model, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="model"/> from now on, unless it already is the board, and adds the rows for its relays and
    /// inputs. Doesn't save the choice: the setup guide does.
    /// </summary>
    public async Task UseAsync(BoardModel model, CancellationToken cancellationToken)
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "This installation was in use before the setup guide chose the relay board: it keeps the {Model}")]
    private partial void LogEarlierModelSaved(string model);
}
