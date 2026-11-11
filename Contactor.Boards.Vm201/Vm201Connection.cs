using Microsoft.Extensions.Options;

namespace Contactor.Boards.Vm201;

/// <summary>The VM201's connection as an <see cref="IBoardConnection"/>; the connection itself is <see cref="Vm201Client"/>'s.</summary>
public sealed class Vm201Connection : IBoardConnection
{
    private readonly Vm201Client _client;
    private readonly Vm201Options _options;

    public Vm201Connection(Vm201Client client, IOptions<Vm201Options> options)
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
        _client.SetLogin(Vm201Login.TryCreate(login.Username, login.Password, out var error)
            ?? throw new ArgumentException(error, nameof(login)));
    }

    public string? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Vm201Login.TryCreate(login.Username, login.Password, out var error);
        return error;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
