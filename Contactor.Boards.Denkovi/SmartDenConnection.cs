using Microsoft.Extensions.Options;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// The smartDEN's connection as an <see cref="IBoardConnection"/>; the connection itself is
/// <see cref="SmartDenClient"/>'s. Modbus has no login (<see cref="SmartDenModels"/>), so the only login there is, is
/// none.
/// </summary>
public sealed class SmartDenConnection : IBoardConnection
{
    private readonly SmartDenClient _client;
    private readonly SmartDenOptions _options;

    public SmartDenConnection(SmartDenClient client, IOptions<SmartDenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);

    public BoardLogin DefaultLogin => BoardLogin.None;

    public BoardEndpoint Endpoint => _client.Endpoint;

    public BoardLogin Login => BoardLogin.None;

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        _client.SetEndpoint(endpoint);
    }

    /// <summary>Takes no login but none; anything else throws, as <see cref="CheckLogin"/> refuses it.</summary>
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
        return login.IsNone ? null : new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: DenkoviBrand.Name);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
