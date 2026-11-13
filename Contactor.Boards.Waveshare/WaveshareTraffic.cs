namespace Contactor.Boards.Waveshare;

/// <summary>
/// The Waveshare board's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="WaveshareClient.Traced"/> reports, already in plain words (<see cref="WaveshareTraceText"/>). The polls
/// and the answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class WaveshareTraffic : IBoardTraffic, IDisposable
{
    private readonly WaveshareClient _client;

    public WaveshareTraffic(WaveshareClient client)
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
