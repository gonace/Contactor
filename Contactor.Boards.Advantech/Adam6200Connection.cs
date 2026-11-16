using Microsoft.Extensions.Options;

namespace Contactor.Boards.Advantech;

public sealed class Adam6200Connection : IBoardConnection
{
    private readonly Adam6200Client _client;
    private readonly Adam6200Options _options;

    public Adam6200Connection(Adam6200Client client, IOptions<Adam6200Options> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);
    public BoardLogin DefaultLogin => BoardLogin.None;
    public BoardEndpoint Endpoint => _client.Endpoint;
    public BoardLogin Login => BoardLogin.None;
    public void SetEndpoint(BoardEndpoint endpoint) => _client.SetEndpoint(endpoint);
    public void SetLogin(BoardLogin login)
    {
        if (CheckLogin(login) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(login));
        }
    }
    public LoginProblem? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.IsNone ? null : new(LoginProblemKind.NoLoginWebPage, Board: "ADAM module");
    }
    public Task RunAsync(CancellationToken cancellationToken) => _client.RunAsync(cancellationToken);
}
