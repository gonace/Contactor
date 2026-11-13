using Microsoft.Extensions.Options;

namespace Contactor.Boards.Devantech;

/// <summary>
/// The Devantech board's connection as an <see cref="IBoardConnection"/>; the connection itself is
/// <see cref="DevantechClient"/>'s. Its login is the board's TCP password alone: the user name is always empty.
/// </summary>
public sealed class DevantechConnection : IBoardConnection
{
    private readonly DevantechClient _client;
    private readonly DevantechOptions _options;

    public DevantechConnection(DevantechClient client, IOptions<DevantechOptions> options)
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

    /// <summary>Unlocks with another password; throws for a login <see cref="CheckLogin"/> refuses.</summary>
    public void SetLogin(BoardLogin login)
    {
        if (CheckLogin(login) is { } error)
        {
            throw new ArgumentException(error, nameof(login));
        }

        _client.SetPassword(login.Password);
    }

    public string? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.Username.Length > 0
            ? "The Devantech board takes a password only, no user name."
            : DevantechModels.CheckPassword(login.Password);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
