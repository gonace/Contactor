using System.Text;
using Contactor.Web.Persistence;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Updates;

/// <summary>
/// The folder the app shares with the update helper on the host (<c>/data/updates</c>): the app writes a request in
/// it, and reads the helper's status, its logs and whether it is installed. The helper reads only the request's first
/// word: "update" or "cancel" (see install/contactor-updater.sh).
/// </summary>
public sealed class UpdateFolder
{
    public const string UpdateWord = "update";
    public const string CancelWord = "cancel";

    /// <summary>The database copy the helper asks the app for before an update (<c>update-backup</c>), and puts back when it goes back.</summary>
    public const string BackupFileName = "before-update.db";

    public UpdateFolder(IOptions<UpdateOptions> options, DatabaseLocation database)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        Path = string.IsNullOrWhiteSpace(options.Value.Folder)
            ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(database.Path)) ?? ".", "updates")
            : options.Value.Folder;
    }

    /// <summary>The folder: <see cref="UpdateOptions.Folder"/>, or <c>updates</c> next to the database, as the helper expects it.</summary>
    public string Path { get; }

    public string RequestPath => System.IO.Path.Combine(Path, "request");

    public string StatusPath => System.IO.Path.Combine(Path, "status");

    /// <summary>The helper writes it each time it runs: the app offers "Update now" only when it is there.</summary>
    public string HelperPath => System.IO.Path.Combine(Path, "helper");

    public string LogsFolder => System.IO.Path.Combine(Path, "logs");

    /// <summary>The last list of releases read from GitHub, so a restart doesn't ask again the same day.</summary>
    public string ReleasesPath => System.IO.Path.Combine(Path, "releases.json");

    public string BackupPath => System.IO.Path.Combine(Path, BackupFileName);

    public bool HelperInstalled => File.Exists(HelperPath);

    /// <summary>The helper's status, or null when there is none or it can't be read.</summary>
    public HelperStatus? ReadStatus()
    {
        try
        {
            var info = new FileInfo(StatusPath);
            if (!info.Exists || info.Length > HelperStatus.MaxBytes)
            {
                return null;
            }

            return HelperStatus.Parse(File.ReadAllText(StatusPath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>When the status file last changed; null when there is none.</summary>
    public DateTime? StatusChangedUtc()
    {
        try
        {
            var info = new FileInfo(StatusPath);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the request (<see cref="UpdateWord"/> or <see cref="CancelWord"/>) in one step, replacing any before it.</summary>
    public void WriteRequest(string word)
    {
        if (word is not (UpdateWord or CancelWord))
        {
            throw new ArgumentException("Only \"update\" or \"cancel\".", nameof(word));
        }

        Directory.CreateDirectory(Path);
        var partial = System.IO.Path.Combine(Path, $".request-{Guid.NewGuid():N}");
        File.WriteAllText(partial, word + "\n", Encoding.ASCII);
        File.Move(partial, RequestPath, overwrite: true);
    }

    public bool RequestWaiting => File.Exists(RequestPath);

    public void DeleteRequest()
    {
        try
        {
            File.Delete(RequestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Taken by the helper meanwhile, or not ours to remove.
        }
    }
}
