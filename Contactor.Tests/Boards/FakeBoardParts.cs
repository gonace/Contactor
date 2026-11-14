using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Boards;

/// <summary>
/// A board in memory as a model's own code builds it for one board's row: a <see cref="FakeRelayBoard"/> (or any
/// <see cref="IRelayBoard"/>), its connection, its traffic and one thing only it has, as an <see cref="ActiveBoard"/>.
/// </summary>
internal sealed class FakeBoardParts
{
    public FakeBoardParts(int relayCount = 8, bool connected = true)
        : this(new FakeRelayBoard(relayCount, connected))
    {
    }

    public FakeBoardParts(IRelayBoard board)
    {
        Board = board;
    }

    public IRelayBoard Board { get; }

    /// <summary>The board, when it is a <see cref="FakeRelayBoard"/>.</summary>
    public FakeRelayBoard Fake => (FakeRelayBoard)Board;

    public FakeConnection Connection { get; } = new();

    public FakeTraffic Traffic { get; } = new();

    public FakeExtra Extra { get; } = new();

    /// <summary>A model with the fake board's relays and inputs, which the catalogue doesn't have.</summary>
    public BoardModel FakeModel => new("fake", "Test", "Fake", Board.Capabilities, DefaultPort: 9760, LoginMaxLength: 0);

    /// <summary>The parts as <paramref name="model"/> (the fake model when null), built for the board on <paramref name="boardId"/>.</summary>
    public ActiveBoard Build(int boardId, BoardModel? model = null)
    {
        model ??= FakeModel;
        var app = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var services = ActiveBoard.NewServices(model, app);

        // From factories, so the board's services dispose them as they would their own.
        services.AddSingleton(_ => Board);
        services.AddSingleton(_ => Connection);
        services.AddSingleton(_ => Traffic);
        return ActiveBoard.Build<IRelayBoard, FakeConnection, FakeTraffic>(
            model,
            new BoardSetup(boardId, IsFirst: true, Endpoint: null, Login: null),
            services,
            _ => [Extra]);
    }
}

/// <summary>A board's connection in memory: it runs until cancelled, and remembers where it was pointed.</summary>
internal sealed class FakeConnection : IBoardConnection, IDisposable
{
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile int _runs;

    public bool Running => _running;

    public bool Disposed => _disposed;

    /// <summary>How often it was run.</summary>
    public int Runs => _runs;

    public BoardEndpoint DefaultEndpoint { get; set; } = new("192.168.1.50", 9760);

    public BoardLogin DefaultLogin => BoardLogin.None;

    public BoardEndpoint Endpoint { get; private set; } = new("192.168.1.50", 9760);

    public BoardLogin Login { get; private set; } = BoardLogin.None;

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        Endpoint = endpoint;
    }

    public void SetLogin(BoardLogin login)
    {
        Login = login;
    }

    public string? CheckLogin(BoardLogin login)
    {
        return null;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _running = true;
        _runs++;
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>A board's traffic in memory: <see cref="Trace"/> tells a step.</summary>
internal sealed class FakeTraffic : IBoardTraffic
{
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>Whether anything listens, e.g. to tell a subscription that was let go.</summary>
    public bool HasListeners => Traced is not null;

    public void Trace(string text)
    {
        Traced?.Invoke(this, new BoardTraceEntry(DateTimeOffset.UnixEpoch, BoardTraceDirection.Sent, text, null, false, false));
    }
}

/// <summary>What only a model has, in memory: it remembers whether it was started and stopped.</summary>
internal sealed class FakeExtra : IHostedService
{
    private volatile bool _started;
    private volatile bool _stopped;

    public bool Started => _started;

    public bool Stopped => _stopped;

    /// <summary>Whether the board's connection was running when it was started: it must not be yet.</summary>
    public Func<bool>? ConnectionRunning { get; set; }

    public bool StartedBeforeConnection { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartedBeforeConnection = ConnectionRunning?.Invoke() != true;
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        return Task.CompletedTask;
    }
}
