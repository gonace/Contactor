using Microsoft.Extensions.Options;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// The Waveshare board's connection as an <see cref="IBoardConnection"/>; the connection itself is
/// <see cref="WaveshareClient"/>'s. Modbus has no login (<see cref="WaveshareModels"/>), so the only login there is, is
/// none.
/// </summary>
public sealed class WaveshareConnection : IBoardConnection
{
    private readonly WaveshareClient _client;
    private readonly WaveshareOptions _options;

    public WaveshareConnection(WaveshareClient client, IOptions<WaveshareOptions> options)
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
        return login.IsNone ? null : new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: "Waveshare board");
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
