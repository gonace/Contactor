using Contactor.Web;

namespace Contactor.Tests.Web;

/// <summary>
/// A first argument that is neither one of the app's commands nor an option: the app says so and doesn't start, e.g. for
/// a command run with docker exec in a container whose image doesn't have it.
/// </summary>
public sealed class AppCommandsTests
{
    [Theory]
    [InlineData("setup-statuss")]
    [InlineData("backup")]
    [InlineData("Healthcheck")]
    public void Should_RefuseAWordThatIsNoCommand(string word)
    {
        using var error = new StringWriter { NewLine = "\n" };

        var exitCode = AppCommands.RefuseUnknown([word, "--urls=http://127.0.0.1:0"], error);

        Assert.Equal(AppCommands.UnknownExitCode, exitCode);
        var lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Contactor.Web: \"{word}\" is not a command. Run it without one to start the app, or with one of:", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("  healthcheck ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  reset-sign-in ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  setup-status ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("--urls=http://127.0.0.1:0")]
    [InlineData("--urls", "http://127.0.0.1:0")]
    [InlineData("-e", "Development")]
    [InlineData("/urls", "http://127.0.0.1:0")]
    [InlineData("Logging:LogLevel:Default=Debug")]
    public void Should_StartTheApp_WithoutACommandOrWithOptions(params string[] args)
    {
        using var error = new StringWriter();

        Assert.Null(AppCommands.RefuseUnknown(args, error));
        Assert.Equal("", error.ToString());
    }
}
