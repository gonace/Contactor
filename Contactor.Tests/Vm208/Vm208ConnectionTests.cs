using Contactor.Boards;
using Contactor.Boards.Vm208;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm208;

/// <summary>The VM208's connection: its address from the settings, and no login, since its API takes none.</summary>
public sealed class Vm208ConnectionTests
{
    [Fact]
    public void Should_TakeNoLoginButNone()
    {
        var options = Options.Create(new Vm208Options { Host = "192.168.1.60", Port = 8080 });
        using var client = new Vm208Client(options, TimeProvider.System, NullLogger<Vm208Client>.Instance);
        var connection = new Vm208Connection(client, options);

        Assert.Equal(new BoardEndpoint("192.168.1.60", 8080), connection.DefaultEndpoint);
        Assert.Equal(BoardLogin.None, connection.DefaultLogin);
        Assert.Equal(BoardLogin.None, connection.Login);
        Assert.Null(connection.CheckLogin(BoardLogin.None));
        Assert.Equal("The VM208 takes no login: its password only protects its own web pages.", connection.CheckLogin(new BoardLogin("admin", "")));
        Assert.NotNull(connection.CheckLogin(new BoardLogin("", "secret")));
        connection.SetLogin(BoardLogin.None);
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "secret")));
        Assert.False(Vm208Model.Model.HasLogin);
    }

    [Fact]
    public void Should_MoveTheClient_WhenTheAddressChanges()
    {
        var options = Options.Create(new Vm208Options { Host = "192.168.1.60" });
        using var client = new Vm208Client(options, TimeProvider.System, NullLogger<Vm208Client>.Instance);
        var connection = new Vm208Connection(client, options);

        connection.SetEndpoint(new BoardEndpoint("192.168.1.61", 81));

        Assert.Equal(new BoardEndpoint("192.168.1.61", 81), connection.Endpoint);
        Assert.Equal(new BoardEndpoint("192.168.1.60", 80), connection.DefaultEndpoint);
    }
}
