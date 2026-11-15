using Contactor.Web.Features.Health;
using Contactor.Web.Features.Setup;

namespace Contactor.Web;

/// <summary>
/// The commands the app runs instead of starting (<c>dotnet Contactor.Web.dll &lt;command&gt;</c>), and what it does with
/// a first argument that is neither one of them nor an option: it says so and exits, never starting the app. A command
/// an older image doesn't have, run with <c>docker exec</c> in a running container, would otherwise start a second
/// copy of the app next to it.
/// </summary>
public static class AppCommands
{
    /// <summary>The exit code for a word that isn't a command, as for any wrong usage.</summary>
    public const int UnknownExitCode = 2;

    /// <summary>
    /// Writes a short usage to <paramref name="error"/> and returns <see cref="UnknownExitCode"/> when the first of
    /// <paramref name="args"/> is a word that isn't an option (the app's own commands are handled before this);
    /// otherwise null, and the app starts. An option is what configuration reads from the command line:
    /// <c>--key=value</c>, <c>--key value</c>, <c>-key</c>, <c>/key value</c> or <c>key=value</c>.
    /// </summary>
    public static int? RefuseUnknown(string[] args, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [var first, ..] || IsOption(first))
        {
            return null;
        }

        error.WriteLine($"Contactor.Web: \"{first}\" is not a command. Run it without one to start the app, or with one of:");
        error.WriteLine($"  {HealthProbe.Command} [url]   whether the running app answers");
        error.WriteLine($"  {SignInReset.Command}       forgets how people sign in, so the setup guide opens again");
        error.WriteLine($"  {SetupStatus.Command}        how far the setup is");
        return UnknownExitCode;
    }

    private static bool IsOption(string argument)
    {
        return argument.StartsWith('-') || argument.StartsWith('/') || argument.Contains('=', StringComparison.Ordinal);
    }
}
