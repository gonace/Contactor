using Contactor.Boards;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.DAEnetIP2v2;

public sealed class DAEnetIP2v2Connection : IBoardConnection
{
    private readonly DAEnetIP2v2Client _client;
    private readonly DAEnetIP2v2Options _options;

    public DAEnetIP2v2Connection(DAEnetIP2v2Client client, IOptions<DAEnetIP2v2Options> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);
    public BoardLogin DefaultLogin => new(_options.Username, _options.Password);
    public BoardEndpoint Endpoint => _client.Endpoint;
    public BoardLogin Login => _client.Login;
    public void SetEndpoint(BoardEndpoint endpoint) => _client.SetEndpoint(endpoint);
    public void SetLogin(BoardLogin login)
    {
        if (CheckLogin(login) is { } error) throw new ArgumentException(error, nameof(login));
        _client.SetLogin(login);
    }
    public string? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.Username.Length <= BoardLogin.MaxLength && login.Password.Length <= BoardLogin.MaxLength
            ? null
            : "The user name and password can be at most 64 characters.";
    }
    public Task RunAsync(CancellationToken cancellationToken) => _client.RunAsync(cancellationToken);
}
