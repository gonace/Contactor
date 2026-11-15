using Contactor.Boards;
using Microsoft.Extensions.Logging;

namespace Contactor.Boards.DAEnetIP2v2;

public sealed partial class DAEnetIP2v2Board : IRelayBoard
{
    private readonly DAEnetIP2v2Client _client;
    private readonly ILogger<DAEnetIP2v2Board> _logger;

    public DAEnetIP2v2Board(DAEnetIP2v2Client client, ILogger<DAEnetIP2v2Board> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;
    public BoardCapabilities Capabilities => _client.Model.Capabilities;
    public BoardState State => ToBoardState(_client.State);

    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default) =>
        new(await _client.TurnOnAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default) =>
        new(await _client.TurnOffAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));

    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The DAEnetIP2v2 relay API does not expose a verified pulse operation.");

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public static BoardState ToBoardState(DAEnetIP2v2State state) =>
        new(state.IsConnected, new ChannelMask(state.Relays), ChannelMask.None, state.LastContactUtc);

    private ulong ToMask(ChannelMask relays)
    {
        if (!Capabilities.AllRelays.ContainsAll(relays))
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"This model has relays 1–{Capabilities.RelayCount} only.");
        return relays.Bits;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A relay-board state subscriber failed.")]
    private partial void LogSubscriberFailed(Exception exception);

    private void OnStateChanged(object? sender, DAEnetIP2v2State state)
    {
        if (StateChanged is not { } handlers) return;
        var boardState = ToBoardState(state);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try { handler(this, boardState); }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            { LogSubscriberFailed(ex); }
        }
    }
}
