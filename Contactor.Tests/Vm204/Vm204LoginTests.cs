using Contactor.Boards;
using Contactor.Boards.Vm204;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm204;

/// <summary>Which logins the VM204's API can take: its buffers' lengths and characters sent unchanged.</summary>
public sealed class Vm204LoginTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("admin", "VM204")]
    [InlineData("Az09-._~", "!$'()*+,;:@")]
    public void Should_TakeALogin_TheBoardCanCheck(string username, string password)
    {
        var login = Vm204Login.TryCreate(username, password, out var error);

        Assert.Null(error);
        Assert.Equal((username, password), (login!.Username, login.Password));
    }

    [Fact]
    public void Should_TakeUpTo31CharactersEach_And48Together()
    {
        Assert.NotNull(Vm204Login.TryCreate(new string('a', 31), new string('b', 17), out _));
        Assert.Null(Vm204Login.TryCreate(new string('a', 32), "", out var tooLong));
        Assert.Equal("The user name can be at most 31 characters on the relay board.", tooLong);
        Assert.Null(Vm204Login.TryCreate(new string('a', 31), new string('b', 18), out var together));
        Assert.Equal("The user name and password can be at most 48 characters together on the relay board.", together);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a=b")]
    [InlineData("50%")]
    [InlineData("a b")]
    [InlineData("a#b")]
    [InlineData("a?b")]
    [InlineData("a/b")]
    [InlineData("åsa")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    public void Should_RefuseCharactersTheBoardWouldNotGetUnchanged(string password)
    {
        Assert.Null(Vm204Login.TryCreate("admin", password, out var error));
        Assert.Equal("The password can only contain letters a–z, digits and - . _ ~ ! $ ' ( ) * + , ; : @ on the relay board.", error);
    }

    [Fact]
    public void Should_NeverShowThePassword()
    {
        Assert.Equal("login as admin", Vm204Login.TryCreate("admin", "VM204", out _)!.ToString());
        Assert.Equal("no login", Vm204Login.None.ToString());
    }

    [Fact]
    public void Should_CheckLogins_ThroughTheConnection()
    {
        var options = Options.Create(new Vm204Options { Host = "127.0.0.1", Username = "admin", Password = "VM204" });
        using var client = new Vm204Client(options, TimeProvider.System, NullLogger<Vm204Client>.Instance);
        var connection = new Vm204Connection(client, options);

        Assert.Null(connection.CheckLogin(new BoardLogin("admin", "x")));
        Assert.NotNull(connection.CheckLogin(new BoardLogin("admin", "x&y")));
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "x&y")));
        Assert.Equal(new BoardLogin("admin", "VM204"), connection.DefaultLogin);
        Assert.Equal(new BoardEndpoint("127.0.0.1", 80), connection.DefaultEndpoint);

        connection.SetLogin(new BoardLogin("user", "pw"));
        Assert.Equal(new BoardLogin("user", "pw"), connection.Login);
    }
}
