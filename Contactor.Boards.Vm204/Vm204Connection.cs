using Microsoft.Extensions.Options;

namespace Contactor.Boards.Vm204;

/// <summary>The VM204's connection as an <see cref="IBoardConnection"/>; the connection itself is <see cref="Vm204Client"/>'s.</summary>
public sealed class Vm204Connection : IBoardConnection
{
    private readonly Vm204Client _client;
    private readonly Vm204Options _options;

    public Vm204Connection(Vm204Client client, IOptions<Vm204Options> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);

    // The client checked the settings' login when it was created.
    public BoardLogin DefaultLogin => new(_options.Username, _options.Password);

    public BoardEndpoint Endpoint => _client.Endpoint;

    public BoardLogin Login => new(_client.Login.Username, _client.Login.Password);

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        _client.SetEndpoint(endpoint);
    }

    public void SetLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        _client.SetLogin(Vm204Login.TryCreate(login.Username, login.Password, out var error)
            ?? throw new ArgumentException(error, nameof(login)));
    }

    public string? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Vm204Login.TryCreate(login.Username, login.Password, out var error);
        return error;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
