using Microsoft.Extensions.Options;

namespace Contactor.Boards.Shelly;

/// <summary>
/// The Shelly's connection as an <see cref="IBoardConnection"/>; the connection itself is <see cref="ShellyClient"/>'s.
/// Its login is the device's password alone: the digest user is always "admin", so the user name is always empty.
/// </summary>
public sealed class ShellyConnection : IBoardConnection
{
    private readonly ShellyClient _client;
    private readonly ShellyOptions _options;

    public ShellyConnection(ShellyClient client, IOptions<ShellyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);

    // The client checked the settings' password when it was created.
    public BoardLogin DefaultLogin => new("", _options.Password ?? "");

    public BoardEndpoint Endpoint => _client.Endpoint;

    public BoardLogin Login => new("", _client.Password);

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        _client.SetEndpoint(endpoint);
    }

    /// <summary>Connects again with another password; throws for a login <see cref="CheckLogin"/> refuses.</summary>
    public void SetLogin(BoardLogin login)
    {
        if (CheckLogin(login) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(login));
        }

        _client.SetPassword(login.Password);
    }

    public LoginProblem? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.Username.Length > 0
            ? new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: ShellyModels.Brand)
            : ShellyModels.CheckPassword(login.Password);
    }

    public void TryAgain()
    {
        _client.TryAgain();
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
