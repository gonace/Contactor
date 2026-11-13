using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// One model of board, built while the app runs when it is chosen: its adapters, what only it has (e.g. the VM201's
/// timers), and its own small set of services, which it owns and disposes. <see cref="BoardSwitch"/> runs it while it
/// is the chosen board.
/// </summary>
public sealed class ActiveBoard : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private int _disposed;

    private ActiveBoard(
        BoardModel model,
        ServiceProvider services,
        IRelayBoard board,
        IBoardConnection connection,
        IBoardTraffic traffic,
        IReadOnlyList<IHostedService> extras)
    {
        Model = model;
        _services = services;
        Board = board;
        Connection = connection;
        Traffic = traffic;
        Extras = extras;
    }

    public BoardModel Model { get; }

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
    /// when it is chosen, and not later on the connection. <paramref name="extras"/> makes what only this model has.
    /// </summary>
    public static ActiveBoard Build<TBoard, TConnection, TTraffic>(
        BoardModel model,
        IServiceCollection services,
        Func<IServiceProvider, IReadOnlyList<IHostedService>>? extras = null)
        where TBoard : class, IRelayBoard
        where TConnection : class, IBoardConnection
        where TTraffic : class, IBoardTraffic
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            return new ActiveBoard(
                model,
                provider,
                provider.GetRequiredService<TBoard>(),
                provider.GetRequiredService<TConnection>(),
                provider.GetRequiredService<TTraffic>(),
                extras?.Invoke(provider) ?? []);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
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
