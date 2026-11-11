using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

public sealed class Vm201LoginTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", "")]
    [InlineData("admin", "secret")]
    [InlineData("12345678", "!#%&/()=")]
    public void Should_AcceptWhatTheBoardCanTake(string username, string password)
    {
        var login = Vm201Login.TryCreate(username, password, out var error);

        Assert.NotNull(login);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("123456789", "", "The user name can be at most 8 characters on the relay board.")]
    [InlineData("", "123456789", "The password can be at most 8 characters on the relay board.")]
    [InlineData("åsa", "", "The user name can only contain letters a–z, digits and plain symbols.")]
    [InlineData("", "lösen", "The password can only contain letters a–z, digits and plain symbols.")]
    public void Should_RefuseWhatTheBoardCannotTake(string username, string password, string message)
    {
        Assert.Null(Vm201Login.TryCreate(username, password, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void Should_NeverPrintThePassword()
    {
        var login = Vm201Login.TryCreate("admin", "secret", out _)!;

        Assert.DoesNotContain("secret", login.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", $"{login}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogIn_WhenGivenTheRightLoginWhileRunning()
    {
        await using var board = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        board.Start();
        var client = new Vm201Client(
            Options.Create(new Vm201Options { Host = "127.0.0.1", Port = board.Port, Username = "admin", Password = "wrong" }),
            TimeProvider.System,
            NullLogger<Vm201Client>.Instance);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => board.DeniedLoginCount >= 1);

        client.SetLogin(Vm201Login.TryCreate("admin", "secret", out _)!);

        await WaitUntilAsync(() => client.State.IsConnected);
        Assert.Equal("admin", client.Login.Username);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
