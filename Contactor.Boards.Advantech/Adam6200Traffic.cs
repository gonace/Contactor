namespace Contactor.Boards.Advantech;

public sealed class Adam6200Traffic : IBoardTraffic, IDisposable
{
    private readonly Adam6200Client _client;

    public Adam6200Traffic(Adam6200Client client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;
    public void Dispose() => _client.Traced -= OnTraced;
    private void OnTraced(object? sender, BoardTraceEntry entry) => Traced?.Invoke(this, entry);
}
