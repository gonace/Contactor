namespace Contactor.Boards.Shelly;

/// <summary>
/// The Shelly's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="ShellyClient.Traced"/> reports, already in plain words (<see cref="ShellyTraceText"/>). The polls and the
/// answers that change nothing are routine.
/// </summary>
public sealed class ShellyTraffic : IBoardTraffic, IDisposable
{
    private readonly ShellyClient _client;

    public ShellyTraffic(ShellyClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;

    public void Dispose()
    {
        _client.Traced -= OnTraced;
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        Traced?.Invoke(this, entry);
    }
}
