namespace Contactor.Boards.Devantech;

/// <summary>
/// The Devantech board's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="DevantechClient.Traced"/> reports, already in plain words (<see cref="DevantechTraceText"/>). The polls
/// and the answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class DevantechTraffic : IBoardTraffic, IDisposable
{
    private readonly DevantechClient _client;

    public DevantechTraffic(DevantechClient client)
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
