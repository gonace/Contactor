namespace Contactor.Boards.Advantech;

public sealed class Adam6000Traffic : IBoardTraffic, IDisposable
{
    private readonly Adam6000Client _client;

    public Adam6000Traffic(Adam6000Client client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;

    public void Dispose() => _client.Traced -= OnTraced;

    private void OnTraced(object? sender, BoardTraceEntry entry) => Traced?.Invoke(this, entry);
}
