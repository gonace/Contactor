using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Advantech;

public sealed class Adam6000Board : IRelayBoard
{
    private readonly Adam6000Client _client;
    private readonly ILogger<Adam6000Board> _logger;

    public Adam6000Board(Adam6000Client client, ILogger<Adam6000Board> logger)
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

    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Pulse output is not supported by the ADAM-6000 relay integration.");
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    public static BoardState ToBoardState(Adam6000State state) => new(state.IsConnected, new(state.Relays), new(state.Inputs), state.LastContactUtc);

    private ulong ToMask(ChannelMask mask)
    {
        if (!Capabilities.AllRelays.ContainsAll(mask))
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }
        return mask.Bits;
    }

    private void OnStateChanged(object? sender, Adam6000State state)
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
            catch (Exception ex) { Adam6000Log.StateSubscriberFailed(_logger, ex); }
#pragma warning restore CA1031
        }
    }
}

internal static partial class Adam6000Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "An ADAM-6000 state subscriber failed.")]
    internal static partial void StateSubscriberFailed(ILogger logger, Exception exception);
}
