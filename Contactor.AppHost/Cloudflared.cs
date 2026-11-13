using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Contactor.AppHost;

/// <summary>
/// Gives the web app the cloudflared the image has: the version the Dockerfile pins, downloaded once from Cloudflare's
/// GitHub release into the AppHost's own folder, and checked against its SHA-256 before every run. Nothing already on
/// the computer is looked for or used. No Aspire types here, so the tests compile this file on its own.
/// </summary>
public static class Cloudflared
{
    /// <summary>
    /// The version the Dockerfile's <c>cloudflare/cloudflared</c> image has. Raise both together, with the new
    /// builds' SHA-256 in <see cref="Builds"/>.
    /// </summary>
    public const string Version = "2026.10.7";

    /// <summary>A release file larger than this is not downloaded (each one is about 20–55 MB).</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Each system's build: its file in the release, and the SHA-256 of the cloudflared program (for macOS, the program
    /// inside the <c>.tgz</c>). Keyed by .NET's runtime identifiers.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Build> Builds = new Dictionary<string, Build>(StringComparer.Ordinal)
    {
        ["linux-x64"] = new("cloudflared-linux-amd64", "1f99fb518b83dec2ba8d870a7cc991d82a77e6196295a3d2754ba17ab9f91872"),
        ["linux-arm64"] = new("cloudflared-linux-arm64", "3df43aedc9ff291d1735b5cb576cb97e0b157488796db2e32b28aecabb445775"),
        ["win-x64"] = new("cloudflared-windows-amd64.exe", "2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343"),
        // Cloudflare has no Arm64 build for Windows; Windows on Arm runs the x64 one.
        ["win-arm64"] = new("cloudflared-windows-amd64.exe", "2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343"),
        ["osx-x64"] = new("cloudflared-darwin-amd64.tgz", "5c55e77da7012cebca19170357e196176c48c9647cc2ead404702fa125ddbd86"),
        ["osx-arm64"] = new("cloudflared-darwin-arm64.tgz", "2136431d5eb467bf447d2ee514b713e192719b143aec1d9e09bf7dc290d5bb12"),
    };

    /// <summary>One system's build of cloudflared.</summary>
    public sealed record Build(string Asset, string Sha256)
    {
        /// <summary>Whether the release file is a <c>.tgz</c> with the program in it, rather than the program itself.</summary>
        public bool Packed => Asset.EndsWith(".tgz", StringComparison.Ordinal);

        /// <summary>Where the release file is downloaded from.</summary>
        public Uri Url => new($"https://github.com/cloudflare/cloudflared/releases/download/{Version}/{Asset}");
    }

    /// <summary>The runtime identifier of the computer the AppHost runs on, e.g. <c>win-x64</c>.</summary>
    public static string HostSystem()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "other";
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{architecture}";
    }

    /// <summary>
    /// The path of a checked cloudflared for <paramref name="system"/> in <paramref name="folder"/>, downloaded first
    /// when it isn't there or doesn't match its SHA-256.
    /// </summary>
    /// <exception cref="CloudflaredUnavailableException">There is no build for the system, or the download is wrong.</exception>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    public static Task<string> GetAsync(string system, string folder, HttpClient http, CancellationToken cancellationToken)
    {
        if (!Builds.TryGetValue(system, out var build))
        {
            throw new CloudflaredUnavailableException($"Cloudflare has no cloudflared {Version} for {system}.");
        }

        var name = system.StartsWith("win-", StringComparison.Ordinal) ? "cloudflared.exe" : "cloudflared";
        return GetAsync(build, Path.Combine(folder, name), http, cancellationToken);
    }

    /// <summary>
    /// <paramref name="path"/> once it holds <paramref name="build"/>'s program: kept when it matches its SHA-256,
    /// downloaded again when it doesn't or isn't there.
    /// </summary>
    public static async Task<string> GetAsync(Build build, string path, HttpClient http, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            if (await MatchesAsync(path, build.Sha256, cancellationToken))
            {
                return path;
            }

            File.Delete(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var download = path + ".download";
        var program = path + ".new";
        try
        {
            await DownloadAsync(http, build.Url, download, cancellationToken);
            if (build.Packed)
            {
                await UnpackAsync(download, program, cancellationToken);
            }
            else
            {
                File.Move(download, program, overwrite: true);
            }

            if (!await MatchesAsync(program, build.Sha256, cancellationToken))
            {
                throw new CloudflaredUnavailableException($"The downloaded {build.Asset} doesn't match its SHA-256, so it was deleted.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(program, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(download);
            File.Delete(program);
        }
    }

    private static async Task DownloadAsync(HttpClient http, Uri url, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new CloudflaredUnavailableException($"{url} is larger than {MaxDownloadBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        await CopyAtMostAsync(source, target, url.ToString(), cancellationToken);
    }

    // Takes the one cloudflared program out of a release's .tgz.
    private static async Task UnpackAsync(string tgz, string program, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(tgz);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && Path.GetFileName(entry.Name) == "cloudflared" && entry.DataStream is { } data)
            {
                await using var target = File.Create(program);
                await CopyAtMostAsync(data, target, tgz, cancellationToken);
                return;
            }
        }

        throw new CloudflaredUnavailableException($"{Path.GetFileName(tgz)} has no cloudflared in it.");
    }

    private static async Task CopyAtMostAsync(Stream source, Stream target, string what, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new CloudflaredUnavailableException($"{what} is larger than {MaxDownloadBytes} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexStringLower(hash) == sha256;
    }
}

/// <summary>cloudflared can't be had for this computer: there is no build for it, or the download is wrong.</summary>
public sealed class CloudflaredUnavailableException : Exception
{
    public CloudflaredUnavailableException(string message)
        : base(message)
    {
    }
}
