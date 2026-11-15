using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Updates;

/// <summary>A published release: its version, the day it came out, and what changed in it.</summary>
/// <param name="Version">E.g. <c>2026.11.20</c>.</param>
/// <param name="Date">The day it was published (UTC); null when GitHub didn't say.</param>
/// <param name="Changes">One line per change, from the release's notes; empty when it lists none.</param>
public sealed record Release(string Version, DateOnly? Date, IReadOnlyList<string> Changes);

/// <summary>Where the list of releases comes from: GitHub, or a test's own.</summary>
public interface IReleaseSource
{
    /// <summary>Every published release, newest first. Throws when the list can't be had.</summary>
    Task<IReadOnlyList<Release>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the releases from GitHub's public API (<see cref="UpdateOptions.ReleasesUrl"/>), without a login. The Images
/// workflow makes a release for each published and signed version, with notes made from the titles of the pull
/// requests merged since the one before; pull requests labelled <c>internal</c> are left out of them
/// (<c>.github/release.yml</c>).
/// </summary>
public sealed partial class GitHubReleases : IReleaseSource
{
    public const string HttpClientName = "github-releases";

    /// <summary>A release list this big is not GitHub's.</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    // The most changes shown for one release, and the longest line.
    private const int MaxChanges = 30;
    private const int MaxChangeLength = 200;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UpdateOptions _options;

    public GitHubReleases(IHttpClientFactory httpClientFactory, IOptions<UpdateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<Release>> GetAsync(CancellationToken cancellationToken)
    {
        // None to read, e.g. the demo's copies of the app.
        if (string.IsNullOrEmpty(_options.ReleasesUrl))
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ReleasesUrl);
        // GitHub asks for a User-Agent; it says only which app asks, nothing about the installation.
        request.Headers.UserAgent.ParseAdd("Contactor");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (limited.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("GitHub's list of releases is too big.");
            }

            await limited.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }

        return Parse(limited.ToArray());
    }

    /// <summary>
    /// The releases in GitHub's JSON, newest version first. Drafts, pre-releases and tags that aren't a version are left
    /// out. Throws <see cref="JsonException"/> on what isn't a list of releases.
    /// </summary>
    public static IReadOnlyList<Release> Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Not a list of releases.");
        }

        var releases = new List<(ReleaseVersion Version, Release Release)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Bool(item, "draft") || Bool(item, "prerelease")
                || !ReleaseVersion.TryParse(Text(item, "tag_name"), out var version)
                || releases.Any(r => r.Version == version))
            {
                continue;
            }

            var date = DateTime.TryParse(Text(item, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var published)
                ? DateOnly.FromDateTime(published)
                : (DateOnly?)null;
            releases.Add((version, new Release(version.Text, date, Changes(Text(item, "body")))));
        }

        return [.. releases.OrderByDescending(r => r.Version).Select(r => r.Release)];
    }

    /// <summary>
    /// The changes in a release's notes: each list item, as GitHub writes them ("* Title by @someone in https://…"),
    /// without who made it and the link. Headings, "Full Changelog" and anything else are left out.
    /// </summary>
    public static IReadOnlyList<string> Changes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return [];
        }

        var changes = new List<string>();
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim();
            if (!(line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)))
            {
                continue;
            }

            var change = ByAndLink().Replace(line[2..], "").Trim();
            if (change.Length == 0 || change.StartsWith('@'))
            {
                continue;
            }

            changes.Add(change.Length > MaxChangeLength ? $"{change[..(MaxChangeLength - 1)]}…" : change);
            if (changes.Count == MaxChanges)
            {
                break;
            }
        }

        return changes;
    }

    private static string? Text(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool Bool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    // " by @someone in https://github.com/…/pull/12", or a bare " in https://…" or "(#12)" at the end.
    [GeneratedRegex(@"(\s+by\s+@[\w-]+(\[bot\])?)?(\s+in\s+https?://\S+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ByAndLink();
}
