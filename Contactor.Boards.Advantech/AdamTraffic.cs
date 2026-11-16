namespace Contactor.Boards.Advantech;

/// <summary>
/// The ADAM module's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="AdamClient.Traced"/> reports, already in plain words (<see cref="AdamTraceText"/>). The polls
/// and the answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class AdamTraffic : IBoardTraffic, IDisposable
{
    private readonly AdamClient _client;

    public AdamTraffic(AdamClient client)
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
