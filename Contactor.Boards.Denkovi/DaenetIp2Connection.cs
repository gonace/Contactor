using Microsoft.Extensions.Options;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// The DAEnetIP2 v2's connection as an <see cref="IBoardConnection"/>; the connection itself is
/// <see cref="DaenetIp2Client"/>'s. Its login is the board's read-write community alone, as a password: the user name is
/// always empty. The manual says the read-write community is used in read and write operations (section 6.6.1), so one
/// secret does both. A wrong community gets no answer at all, which the login's hint says in words.
/// </summary>
public sealed class DaenetIp2Connection : IBoardConnection
{
    private readonly DaenetIp2Client _client;
    private readonly DaenetIp2Options _options;

    public DaenetIp2Connection(DaenetIp2Client client, IOptions<DaenetIp2Options> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public BoardEndpoint DefaultEndpoint => new(_options.Host, _options.Port);

    // The client checked the settings' community when it was created.
    public BoardLogin DefaultLogin => new("", _options.Community ?? "");

    public BoardEndpoint Endpoint => _client.Endpoint;

    public BoardLogin Login => new("", _client.Community);

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        _client.SetEndpoint(endpoint);
    }

    /// <summary>
    /// Asks with another community; throws for a login <see cref="CheckLogin"/> refuses, except
    /// <see cref="BoardLogin.None"/>: no community yet, which the board doesn't answer (see <see cref="IBoardConnection.SetLogin"/>).
    /// </summary>
    public void SetLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (!login.IsNone && CheckLogin(login) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(login));
        }

        _client.SetCommunity(login.Password);
    }

    /// <summary>
    /// Takes the read-write community as a password alone, 4 to 12 characters: the board always has one, so no login is
    /// refused too.
    /// </summary>
    public LoginProblem? CheckLogin(BoardLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.Username.Length > 0
            ? new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: DenkoviBrand.Name)
            : DaenetIp2Models.CheckCommunity(login.Password);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _client.RunAsync(cancellationToken);
    }
}
