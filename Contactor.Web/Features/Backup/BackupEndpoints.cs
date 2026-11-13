using System.IO.Compression;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contactor.Web.Features.Backup;

public static class BackupEndpoints
{
    public const string LatestPath = "/settings/backup";

    /// <summary>
    /// <c>/settings/backup</c>: downloads the newest copy of the database as a zip, so it can be kept off the
    /// Raspberry Pi. A zip rather than the bare <c>.db</c>: browsers may warn about an unknown file type, and the copy
    /// shrinks a lot. Behind the sign-in like every page. Only ever a file the backup itself wrote, by its own
    /// name.
    /// </summary>
    public static IEndpointRouteBuilder MapBackupGroup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LatestPath, GetLatestAsync).WithTags("Backup").ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<Results<PushStreamHttpResult, NotFound>> GetLatestAsync(BackupService backup, CancellationToken cancellationToken)
    {
        var latest = await backup.LatestAsync(cancellationToken);
        if (latest is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Stream(
            body => WriteZipAsync(latest, body, cancellationToken),
            "application/zip",
            Path.ChangeExtension(latest.Name, ".zip"),
            latest.LastWriteTimeUtc);
    }

    private const string TemporaryPrefix = ".download-";

    // Zipped into a temporary file next to the copies first: ZipArchive still writes synchronously when it finishes,
    // and Kestrel allows only asynchronous writes to a response. The file is deleted when it is closed. The newest copy
    // is never the one the backup deletes (it keeps the latest 14), so it is still there to open.
    private static async Task WriteZipAsync(FileInfo latest, Stream body, CancellationToken cancellationToken)
    {
        var folder = latest.DirectoryName!;
        RemoveLeftovers(folder);
        await using var temporary = new FileStream(
            Path.Combine(folder, $"{TemporaryPrefix}{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await using (var source = new FileStream(
            latest.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true))
        await using (var zip = await ZipArchive.CreateAsync(temporary, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
        {
            var entry = zip.CreateEntry(latest.Name, CompressionLevel.Optimal);
            entry.LastWriteTime = latest.LastWriteTimeUtc;
            await using var target = await entry.OpenAsync(cancellationToken);
            await source.CopyToAsync(target, cancellationToken);
        }

        temporary.Position = 0;
        await temporary.CopyToAsync(body, cancellationToken);
    }

    // A download cut off by a crash or a restart can leave its temporary file behind; one older than an hour is not in use.
    // Compared with the real clock, not TimeProvider: file times come from the file system's clock.
    private static void RemoveLeftovers(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, $"{TemporaryPrefix}*.zip"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use or already gone.
            }
        }
    }
}
