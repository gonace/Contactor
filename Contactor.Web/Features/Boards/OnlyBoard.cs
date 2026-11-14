using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Until the screens show every board (Issue #4): one board. The temporary bridge for the screens that still show one
/// board only, Settings and the setup guide (the safety services, handlers and other screens each take every board on
/// its own): the <see cref="IRelayBoard"/> of the app's one board, with its address, the same object for the app's lifetime, so a listener stays when
/// the board's row is made in the setup guide or its model is replaced. Before the app has a board it has no relays and never answers, as a board before its model is chosen:
/// commands throw <see cref="BoardNotConnectedException"/>, and nothing connects. With more than one board it throws:
/// what uses it can't tell them apart yet. Reached through <see cref="BoardRegistry.Only"/>; every use is marked
/// "Until the screens show every board (Issue #4): one board." so the screens' PRs find them.
/// </summary>
public sealed partial class OnlyBoard : IRelayBoard, IDisposable
{
    private static readonly BoardEndpoint NoEndpoint = new("", 0);

    private readonly BoardRegistry _registry;
    private readonly ILogger<OnlyBoard> _logger;

    internal OnlyBoard(BoardRegistry registry, ILogger<OnlyBoard> logger)
    {
        _registry = registry;
        _logger = logger;
        _registry.StateChanged += OnStateChanged;
    }

    /// <inheritdoc/>
    public event EventHandler<BoardState>? StateChanged;

    /// <summary>The app's one board, or null before it has one. Throws with more than one.</summary>
    public LiveBoard? Board => _registry.All switch
    {
        [] => null,
        [var only] => only,
        _ => throw new InvalidOperationException("The app runs more than one relay board, and this part of it only knows one yet (Issue #4)."),
    };

    /// <summary>The one board's row id, or null before the app has a board.</summary>
    public int? BoardId => Board?.BoardId;

    /// <summary>The one board's model, or null before one is chosen.</summary>
    public BoardModel? Model => Board?.Model;

    public BoardCapabilities Capabilities => Board?.Capabilities ?? LiveBoard.NoBoard;

    public BoardState State => Board?.State ?? BoardState.Initial;

    /// <summary>The one board's address in use; none before the app has a board.</summary>
    public BoardEndpoint Endpoint => Board?.Endpoint ?? NoEndpoint;

    /// <summary>The one board's login in use; none before the app has a board. A secret: never log it.</summary>
    public BoardLogin Login => Board?.Login ?? BoardLogin.None;

    public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Chosen().TurnOnAsync(relays, cancellationToken);
    }

    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        return Chosen().PulseAsync(relays, length, cancellationToken);
    }

    public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return Chosen().TurnOffAsync(relays, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return Chosen().RefreshAsync(cancellationToken);
    }

    public void Dispose()
    {
        _registry.StateChanged -= OnStateChanged;
    }

    private const string NoBoardYet = "No relay board has been chosen yet.";

    private LiveBoard Chosen()
    {
        return Board ?? throw new BoardNotConnectedException(NoBoardYet);
    }

    // Only the one board's; with more than one, nothing: what listens here can't tell them apart.
    private void OnStateChanged(object? sender, BoardStateChange change)
    {
        if (_registry.All is not [var only] || only.BoardId != change.BoardId || StateChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try
            {
                handler(this, change.State);
            }
#pragma warning disable CA1031 // A subscriber's failure is logged; the others still get the state.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the relay board's state failed")]
    private partial void LogSubscriberFailed(Exception exception);
}
