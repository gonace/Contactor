using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// One model of board for one board's row, built while the app runs when it is chosen: its adapters, what only it has
/// (e.g. the VM201's timers), and its own small set of services, which it owns and disposes. Its board's
/// <see cref="LiveBoard"/> runs it while it is that board's model.
/// </summary>
public sealed class ActiveBoard : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private int _disposed;

    private ActiveBoard(
        BoardModel model,
        int boardId,
        ServiceProvider services,
        IRelayBoard board,
        IBoardConnection connection,
        IBoardTraffic traffic,
        IReadOnlyList<IHostedService> extras)
    {
        Model = model;
        BoardId = boardId;
        _services = services;
        Board = board;
        Connection = connection;
        Traffic = traffic;
        Extras = extras;
    }

    public BoardModel Model { get; }

    /// <summary>The board's row (<see cref="Persistence.Board.Id"/>) it was built for.</summary>
    public int BoardId { get; }

    public IRelayBoard Board { get; }

    public IBoardConnection Connection { get; }

    public IBoardTraffic Traffic { get; }

    /// <summary>The board's own services, e.g. its client.</summary>
    public IServiceProvider Services => _services;

    /// <summary>What only this model has, started before its connection and stopped with it.</summary>
    public IReadOnlyList<IHostedService> Extras { get; }

    /// <summary>
    /// The start of a model's own services: the model, and the app's clock and logging. The model's code adds its
    /// settings, client and adapters, then calls <see cref="Build{TBoard, TConnection, TTraffic}"/>.
    /// </summary>
    public static IServiceCollection NewServices(BoardModel model, IServiceProvider app)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(app);
        var services = new ServiceCollection();
        services.AddSingleton(model);
        services.AddSingleton(app.GetRequiredService<TimeProvider>());
        services.AddSingleton(app.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddOptions();
        return services;
    }

    /// <summary>
    /// Builds the board from its services, resolving everything at once so a setting the board can't take fails here,
    /// when it is chosen, and not later on the connection, and points it at the address and login saved on its row
    /// (<paramref name="setup"/>), if any. <paramref name="extras"/> makes what only this model has.
    /// </summary>
    public static ActiveBoard Build<TBoard, TConnection, TTraffic>(
        BoardModel model,
        BoardSetup setup,
        IServiceCollection services,
        Func<IServiceProvider, IReadOnlyList<IHostedService>>? extras = null)
        where TBoard : class, IRelayBoard
        where TConnection : class, IBoardConnection
        where TTraffic : class, IBoardTraffic
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            var board = new ActiveBoard(
                model,
                setup.BoardId,
                provider,
                provider.GetRequiredService<TBoard>(),
                provider.GetRequiredService<TConnection>(),
                provider.GetRequiredService<TTraffic>(),
                extras?.Invoke(provider) ?? []);
            board.UseSaved(setup.Endpoint, setup.Login);
            return board;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Points the connection at the address and login saved in the app, or the settings' where none is saved. A saved
    /// login this model can't take (saved for another model) falls back to none: the database only holds what a board
    /// accepted, and the settings are checked when the board is built.
    /// </summary>
    public void UseSaved(BoardEndpoint? endpoint, BoardLogin? login)
    {
        Connection.SetEndpoint(endpoint ?? Connection.DefaultEndpoint);
        Connection.SetLogin(LoginFor(login));
    }

    /// <summary>The login in use for a saved one, as <see cref="UseSaved"/> picks it.</summary>
    public BoardLogin LoginFor(BoardLogin? saved)
    {
        var use = saved ?? Connection.DefaultLogin;
        return Connection.CheckLogin(use) is null ? use : BoardLogin.None;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var extra in Extras)
        {
            if (extra is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        await _services.DisposeAsync();
    }
}
