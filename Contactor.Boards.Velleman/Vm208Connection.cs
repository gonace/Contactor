using Microsoft.Extensions.Options;

namespace Contactor.Boards.Velleman;

/// <summary>
/// The VM208's connection as an <see cref="IBoardConnection"/>; the connection itself is <see cref="Vm208Client"/>'s.
/// The board's API takes no login (<see cref="Vm208Model"/>), so the only login there is, is none.
/// </summary>
public sealed class Vm208Connection : IBoardConnection
{
    private readonly Vm208Client _client;
    private readonly Vm208Options _options;

    public Vm208Connection(Vm208Client client, IOptions<Vm208Options> options)
    {
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
        return login.IsNone ? null : new LoginProblem(LoginProblemKind.NoLoginWebPages, Board: "VM208");
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
