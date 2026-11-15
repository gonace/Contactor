using System.Globalization;
using System.Text.RegularExpressions;

namespace Contactor.Web.Features.Updates;

/// <summary>How far an update is, or how it ended, as the update helper writes it to <c>updates/status</c>.</summary>
public static class UpdateStates
{
    public const string Running = "running";
    public const string Done = "done";
    public const string WentBack = "went-back";
    public const string Failed = "failed";

    /// <summary>The helper found no newer version to download.</summary>
    public const string Current = "current";
}

/// <summary>The steps of an update, in order, as the helper writes them.</summary>
public static class UpdateSteps
{
    public const string Download = "download";
    public const string Verify = "verify";
    public const string Backup = "backup";

    /// <summary>Just before the restart, a group is starting or running on: the helper waits for it (up to 10 minutes).</summary>
    public const string Waiting = "waiting";

    public const string Restart = "restart";
    public const string Check = "check";

    /// <summary>The new version didn't come up: the helper goes back to the one before.</summary>
    public const string Back = "back";

    /// <summary>The steps the page lists, in order.</summary>
    public static IReadOnlyList<string> Shown { get; } = [Download, Verify, Backup, Restart, Check];

    /// <summary>Whether the app has gone, or is about to: the splash shows on every open page.</summary>
    public static bool IsRestarting(string? step)
    {
        return step is Restart or Check or Back;
    }
}

/// <summary>Why an update didn't go through, as the helper writes it.</summary>
public static class UpdateReasons
{
    public const string Download = "download";
    public const string Signature = "signature";
    public const string Space = "space";
    public const string Backup = "backup";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
    public const string Health = "health";

    /// <summary>A group was still starting or running on after the helper waited 10 minutes.</summary>
    public const string Switching = "switching";

    public const string Error = "error";
}

/// <summary>
/// The update helper's <c>updates/status</c> file: <c>key=value</c> lines. The app's own user can write that folder
/// too, so it is read like any input: only known keys, only values of the expected form; anything else is left out.
/// </summary>
/// <param name="Id">The update's id, e.g. <c>20261115-140200</c> (UTC); also its log's name.</param>
/// <param name="State">One of <see cref="UpdateStates"/>.</param>
/// <param name="Step">One of <see cref="UpdateSteps"/> while it runs, else null.</param>
/// <param name="From">The version before, e.g. <c>2026.11.14</c>.</param>
/// <param name="To">The version updated to, once the helper knows it.</param>
/// <param name="Reason">One of <see cref="UpdateReasons"/> when it didn't go through.</param>
/// <param name="StartedUtc">When the helper started it.</param>
/// <param name="ChangedUtc">When the helper last wrote the file.</param>
/// <param name="BackUtc">When it went back to the version before.</param>
/// <param name="NeedMegabytes">For <see cref="UpdateReasons.Space"/>: the room it needs.</param>
/// <param name="FreeMegabytes">For <see cref="UpdateReasons.Space"/>: the room there is.</param>
public sealed partial record HelperStatus(
    string Id,
    string State,
    string? Step,
    string? From,
    string? To,
    string? Reason,
    DateTime StartedUtc,
    DateTime ChangedUtc,
    DateTime? BackUtc = null,
    int? NeedMegabytes = null,
    int? FreeMegabytes = null)
{
    /// <summary>The longest status file read; the helper's is a few hundred bytes.</summary>
    public const int MaxBytes = 4096;

    public bool IsRunning => State == UpdateStates.Running;

    /// <summary>The status in <paramref name="text"/>, or null when it isn't one.</summary>
    public static HelperStatus? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxBytes)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }

        var id = values.GetValueOrDefault("id");
        var state = values.GetValueOrDefault("state");
        if (id is null || !IdPattern().IsMatch(id)
            || state is not (UpdateStates.Running or UpdateStates.Done or UpdateStates.WentBack or UpdateStates.Failed or UpdateStates.Current)
            || Time(values.GetValueOrDefault("started")) is not { } started
            || Time(values.GetValueOrDefault("changed")) is not { } changed)
        {
            return null;
        }

        var step = values.GetValueOrDefault("step");
        var reason = values.GetValueOrDefault("reason");
        return new HelperStatus(
            id,
            state,
            state == UpdateStates.Running && step is UpdateSteps.Download or UpdateSteps.Verify or UpdateSteps.Backup or UpdateSteps.Waiting or UpdateSteps.Restart or UpdateSteps.Check or UpdateSteps.Back ? step : null,
            Version(values.GetValueOrDefault("from")),
            Version(values.GetValueOrDefault("to")),
            reason is UpdateReasons.Download or UpdateReasons.Signature or UpdateReasons.Space or UpdateReasons.Backup
                or UpdateReasons.Cancelled or UpdateReasons.Interrupted or UpdateReasons.Health or UpdateReasons.Switching or UpdateReasons.Error ? reason : null,
            started,
            changed,
            Time(values.GetValueOrDefault("back")),
            Megabytes(values.GetValueOrDefault("need-mb")),
            Megabytes(values.GetValueOrDefault("free-mb")));
    }

    /// <summary>Whether <paramref name="id"/> is an update's id, so it can name a log file.</summary>
    public static bool IsId(string? id)
    {
        return id is not null && IdPattern().IsMatch(id);
    }

    private static string? Version(string? value)
    {
        return value is not null && ReleaseVersion.TryParse(value, out _) ? value : null;
    }

    private static DateTime? Time(string? value)
    {
        return DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? DateTime.SpecifyKind(time, DateTimeKind.Utc)
            : null;
    }

    private static int? Megabytes(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var megabytes) ? megabytes : null;
    }

    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
