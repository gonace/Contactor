using System.Reflection;

namespace Contactor.Web;

/// <summary>
/// The release this build is: <c>YYYY.M.D</c> in UTC, without leading zeros, plus <c>.N</c> for a later release the
/// same day (e.g. "2026.11.4", then "2026.11.4.1"), and the commit it was built from. A build that isn't a release
/// (local runs, Aspire, tests) is <see cref="Development"/>.
/// </summary>
/// <param name="Version">The release version, or <see cref="Development"/>.</param>
/// <param name="Commit">The commit the SDK put after "+" in the informational version; null when there is none.</param>
public sealed record AppBuild(string Version, string? Commit)
{
    /// <summary>The version of every build that isn't a release (see Directory.Build.props).</summary>
    public const string Development = "0.0.0-dev";

    /// <summary>This app's build.</summary>
    public static AppBuild Current { get; } = Of(typeof(AppBuild).Assembly);

    public bool IsRelease => Version != Development;

    /// <summary>The commit shortened to 7 characters, as in the image tag <c>sha-&lt;commit&gt;</c>.</summary>
    public string? ShortCommit => Commit is { Length: > 7 } commit ? commit[..7] : Commit;

    public static AppBuild Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Of(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>The build an informational version such as "2026.11.4+0123abc…" names.</summary>
    public static AppBuild Of(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new AppBuild(Development, null);
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new AppBuild(informationalVersion.Trim(), null);
        }

        var commit = informationalVersion[(plus + 1)..].Trim();
        var version = informationalVersion[..plus].Trim();
        return new AppBuild(version.Length > 0 ? version : Development, commit.Length > 0 ? commit : null);
    }
}
