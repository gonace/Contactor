namespace Contactor.Boards.Denkovi;

/// <summary>
/// The smartDEN's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="SmartDenClient.Traced"/> reports, already in plain words (<see cref="SmartDenTraceText"/>). The polls
/// and the answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class SmartDenTraffic : IBoardTraffic, IDisposable
{
    private readonly SmartDenClient _client;

    public SmartDenTraffic(SmartDenClient client)
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
