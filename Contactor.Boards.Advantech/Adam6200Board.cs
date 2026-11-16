using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Advantech;

public sealed class Adam6200Board : IRelayBoard
{
    private readonly Adam6200Client _client;
    private readonly ILogger<Adam6200Board> _logger;

    public Adam6200Board(Adam6200Client client, ILogger<Adam6200Board> logger)
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
        new(await _client.SetAsync(ToMask(relays), true, cancellationToken).ConfigureAwait(false));

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default) =>
        new(await _client.SetAsync(ToMask(relays), false, cancellationToken).ConfigureAwait(false));

    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pulse output is not supported by the ADAM-6260 relay integration.");

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    public static BoardState ToBoardState(Adam6200State state) => new(state.IsConnected, new(state.Relays), new(0), state.LastContactUtc);

    private ulong ToMask(ChannelMask mask)
    {
        if (!Capabilities.AllRelays.ContainsAll(mask))
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }
        return mask.Bits;
    }

    private void OnStateChanged(object? sender, Adam6200State state)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }
        var value = ToBoardState(state);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try { handler(this, value); }
#pragma warning disable CA1031
            catch (Exception ex) { Adam6200Log.StateSubscriberFailed(_logger, ex); }
#pragma warning restore CA1031
        }
    }
}
