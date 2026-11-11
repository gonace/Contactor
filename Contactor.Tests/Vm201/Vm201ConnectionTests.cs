using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

/// <summary>The VM201's address and login as the app's <see cref="IBoardConnection"/>.</summary>
public sealed class Vm201ConnectionTests
{
    private readonly Vm201Client _client;
    private readonly Vm201Connection _connection;

    public Vm201ConnectionTests()
    {
        var options = Options.Create(new Vm201Options { Host = "192.168.1.50", Port = 9761, Username = "john", Password = "secret" });
        _client = new Vm201Client(options, TimeProvider.System, NullLogger<Vm201Client>.Instance);
        _connection = new Vm201Connection(_client, options);
    }

    [Fact]
    public void Should_StartWithTheSettings()
    {
        Assert.Equal(new BoardEndpoint("192.168.1.50", 9761), _connection.DefaultEndpoint);
        Assert.Equal(new BoardLogin("john", "secret"), _connection.DefaultLogin);
        Assert.Equal(_connection.DefaultEndpoint, _connection.Endpoint);
        Assert.Equal(_connection.DefaultLogin, _connection.Login);
    }

    [Fact]
    public void Should_MoveTheClient_ButKeepTheDefaults()
    {
        _connection.SetEndpoint(new BoardEndpoint("192.168.1.51", 9760));
        _connection.SetLogin(new BoardLogin("jane", "other"));

        Assert.Equal(new BoardEndpoint("192.168.1.51", 9760), _client.Endpoint);
        Assert.Equal(("jane", "other"), (_client.Login.Username, _client.Login.Password));
        Assert.Equal(new BoardLogin("jane", "other"), _connection.Login);
        Assert.Equal(new BoardEndpoint("192.168.1.50", 9761), _connection.DefaultEndpoint);
    }

    [Theory]
    [InlineData("toolongname", "", "The user name can be at most 8 characters on the relay board.")]
    [InlineData("john", "pässwd", "The password can only contain letters a–z, digits and plain symbols.")]
    public void Should_RefuseALoginTheBoardCannotTake(string username, string password, string message)
    {
        var login = new BoardLogin(username, password);

        Assert.Equal(message, _connection.CheckLogin(login));
        Assert.Throws<ArgumentException>(() => _connection.SetLogin(login));
        Assert.Equal("john", _client.Login.Username);
    }

    [Fact]
    public void Should_AcceptNoLogin()
    {
        Assert.Null(_connection.CheckLogin(BoardLogin.None));
    }
}
