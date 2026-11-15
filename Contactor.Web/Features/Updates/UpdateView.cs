using System.Globalization;

namespace Contactor.Web.Features.Updates;

/// <summary>What the Updates box in Settings shows.</summary>
public enum UpdateBoxKind
{
    /// <summary>A development build: not updated from here.</summary>
    Development,

    /// <summary>The newest version runs.</summary>
    Current,

    /// <summary>A newer version can be had: what's new, and "Update now".</summary>
    Available,

    /// <summary>An update is under way: its steps.</summary>
    Running,

    /// <summary>How the last update ended, for a day after.</summary>
    Result,
}

public enum UpdateStepState
{
    Done,
    Now,
    Later,
}

/// <summary>A step of an update under way, e.g. "Downloading 2026.11.20".</summary>
public sealed record UpdateStepView(string Text, UpdateStepState State);

/// <summary>How an update ended: "Updated to 2026.11.20", or what went wrong and that nothing was lost.</summary>
/// <param name="IsAlert">Shown in red: the update didn't go through.</param>
/// <param name="IsDone">It went through: shown with a tick.</param>
public sealed record UpdateResultView(string Title, string Text, bool IsAlert, bool IsDone);

/// <summary>The Updates box, worked out from an <see cref="UpdateSnapshot"/>.</summary>
/// <param name="Running">The version running, e.g. "2026.11.14".</param>
/// <param name="Offered">The newest release newer than it; null when there is none.</param>
/// <param name="WhatsNew">Each newer release with changes listed, newest first.</param>
/// <param name="CheckedLine">"Checked today at 06:12." or "Couldn't check for updates."; null when it doesn't show.</param>
/// <param name="CanUpdate">"Update now" (or "Try again") shows.</param>
/// <param name="NoHelper">This machine has no update helper: the box says where updates come from instead.</param>
/// <param name="From">While it runs: the version before.</param>
/// <param name="To">While it runs: the version it updates to.</param>
/// <param name="CanCancel">While it runs, until the restart.</param>
/// <param name="StartedLine">"Started by John at 14:02. The relays stay as they are."</param>
/// <param name="LogId">The update whose log "Show what happened" opens; null when it doesn't show.</param>
public sealed record UpdateBoxView(
    UpdateBoxKind Kind,
    string Running,
    string? Offered,
    IReadOnlyList<Release> WhatsNew,
    string? CheckedLine,
    UpdateResultView? Result,
    bool CanUpdate,
    bool NoHelper,
    IReadOnlyList<UpdateStepView> Steps,
    string? From,
    string? To,
    bool CanCancel,
    string? StartedLine,
    string? LogId)
{
    /// <summary>The box for <paramref name="snapshot"/>, at <paramref name="clock"/>'s time.</summary>
    public static UpdateBoxView For(UpdateSnapshot snapshot, LocalClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNow;
        var running = snapshot.Build.Version;
        if (!snapshot.Build.IsRelease)
        {
            return Empty(UpdateBoxKind.Development, running, null, [], null, false);
        }

        var newer = snapshot.Newer;
        var offered = newer.Count > 0 ? newer[0].Version : null;
        IReadOnlyList<Release> whatsNew = [.. newer.Where(r => r.Changes.Count > 0)];
        var noHelper = !snapshot.HelperInstalled;
        var checkedLine = Checked(snapshot.Releases, clock);
        var canUpdate = offered is not null && !noHelper;

        // Asked for, not taken by the helper yet: the first step.
        if (snapshot.Pending is { } pending)
        {
            return Empty(UpdateBoxKind.Running, running, offered, [], null, noHelper) with
            {
                Steps = StepsFor(UpdateSteps.Download, pending.To),
                From = running,
                To = pending.To,
                CanCancel = true,
                StartedLine = $"Started by {pending.By} at {Time(pending.AtUtc, clock)}. The relays stay as they are.",
            };
        }

        if (snapshot.Status is { } status && now - status.ChangedUtc <= UpdateService.ResultShownFor)
        {
            var stale = UpdateService.IsStale(status, now);
            if (status.IsRunning && !stale)
            {
                var to = status.To ?? offered ?? "the new version";
                return Empty(UpdateBoxKind.Running, running, offered, [], null, noHelper) with
                {
                    Steps = StepsFor(status.Step ?? UpdateSteps.Download, to),
                    From = status.From ?? running,
                    To = to,
                    CanCancel = status.Step is UpdateSteps.Download or UpdateSteps.Verify or UpdateSteps.Backup or UpdateSteps.Waiting,
                    StartedLine = $"Started{(snapshot.StartedBy is { } by ? $" by {by}" : "")} at {Time(status.StartedUtc, clock)}. The relays stay as they are.",
                };
            }

            if (ResultFor(status, stale, offered, snapshot.StartedBy, clock) is { } result)
            {
                // A version newer still, after one that went through: offered as usual under the result.
                var after = result.IsDone || !result.IsAlert;
                return Empty(UpdateBoxKind.Result, running, offered, after ? whatsNew : [], after ? null : checkedLine, noHelper) with
                {
                    Result = result,
                    CanUpdate = canUpdate,
                    LogId = result.IsAlert ? status.Id : null,
                };
            }
        }

        if (snapshot.NoAnswer)
        {
            return Empty(UpdateBoxKind.Result, running, offered, [], null, noHelper) with
            {
                Result = new UpdateResultView(
                    "The update didn't start",
                    "The update helper on this machine didn't answer. Nothing was changed. Run the install command again to repair it, then try again.",
                    IsAlert: true,
                    IsDone: false),
                CanUpdate = canUpdate,
            };
        }

        return offered is null
            ? Empty(UpdateBoxKind.Current, running, null, [], checkedLine, noHelper)
            : Empty(UpdateBoxKind.Available, running, offered, whatsNew, null, noHelper) with { CanUpdate = canUpdate };
    }

    private static UpdateBoxView Empty(UpdateBoxKind kind, string running, string? offered, IReadOnlyList<Release> whatsNew, string? checkedLine, bool noHelper)
    {
        return new UpdateBoxView(kind, running, offered, whatsNew, checkedLine, null, false, noHelper, [], null, null, false, null, null);
    }

    // How the update ended, in plain words; null for one that has nothing to say (nothing newer, nothing offered).
    private static UpdateResultView? ResultFor(HelperStatus status, bool stale, string? offered, string? by, LocalClock clock)
    {
        var to = status.To ?? offered ?? "the new version";
        if (stale)
        {
            return Failure($"The update to {to} didn't finish", "Nothing was changed. Try again.");
        }

        return status.State switch
        {
            UpdateStates.Done => new UpdateResultView($"Updated to {to}", $"At {Time(status.ChangedUtc, clock)}{(by is null ? "" : $", by {by}")}.", IsAlert: false, IsDone: true),
            UpdateStates.WentBack => Failure(
                $"The update to {to} didn't work",
                $"Contactor went back to {status.From ?? "the version before"} at {Time(status.BackUtc ?? status.ChangedUtc, clock)}. Nothing was lost."),
            UpdateStates.Failed => status.Reason switch
            {
                UpdateReasons.Download => Failure($"Couldn't download {to}", "Nothing was changed. Check the internet connection and try again."),
                UpdateReasons.Signature => Failure($"{Capital(to)} didn't pass the check that it's ours", "So it wasn't installed. Nothing was changed. Try again later."),
                UpdateReasons.Space => Failure($"Not enough room for {to}", Room(status)),
                UpdateReasons.Backup => Failure("Couldn't back up before updating", "Nothing was changed. Try again in a while."),
                UpdateReasons.Cancelled => new UpdateResultView("The update was cancelled", "Nothing was changed.", IsAlert: false, IsDone: false),
                UpdateReasons.Switching => new UpdateResultView(
                    "The update was stopped because the relays were still switching",
                    "Try again later. Nothing was changed.",
                    IsAlert: false,
                    IsDone: false),
                UpdateReasons.Interrupted => Failure($"The update to {to} was interrupted", "Nothing was changed. Try again."),
                _ => Failure($"The update to {to} didn't start", "Nothing was changed. Try again."),
            },
            // The helper found nothing newer than what runs, while GitHub already lists a newer version.
            UpdateStates.Current when offered is not null => new UpdateResultView($"{offered} can't be downloaded yet", "Nothing was changed. Try again later.", IsAlert: false, IsDone: false),
            _ => null,
        };
    }

    private static UpdateResultView Failure(string title, string text)
    {
        return new UpdateResultView(title, text, IsAlert: true, IsDone: false);
    }

    private static string Room(HelperStatus status)
    {
        return status is { NeedMegabytes: { } need, FreeMegabytes: { } free }
            ? $"Nothing was changed. The update needs {Gigabytes(need)} of free disk space, and there's {Gigabytes(free)}. Make room and try again."
            : "Nothing was changed. Make room on the disk and try again.";
    }

    private static string Gigabytes(int megabytes)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.0} GB");
    }

    private static string Capital(string text)
    {
        return text.Length > 0 && char.IsAsciiLetterLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    // The steps, each done, under way or still to come.
    private static IReadOnlyList<UpdateStepView> StepsFor(string current, string to)
    {
        var at = current switch
        {
            UpdateSteps.Back => UpdateSteps.Shown.Count - 1,
            UpdateSteps.Waiting => UpdateSteps.Shown.ToList().IndexOf(UpdateSteps.Backup),
            _ => Math.Max(0, UpdateSteps.Shown.ToList().IndexOf(current)),
        };
        return [.. UpdateSteps.Shown.Select((step, i) => new UpdateStepView(
            step switch
            {
                UpdateSteps.Download => $"Downloading {to}",
                UpdateSteps.Verify => "Checking that it's ours",
                // Just before the restart, while a group is starting or running on.
                UpdateSteps.Backup when current == UpdateSteps.Waiting => "Waiting for the relays to finish switching",
                UpdateSteps.Backup => "Backing up",
                UpdateSteps.Restart => "Restarting",
                _ => "Checking the new version",
            },
            i < at ? UpdateStepState.Done : i == at ? UpdateStepState.Now : UpdateStepState.Later))];
    }

    // "Checked today at 06:12.", or that it couldn't be checked.
    private static string? Checked(ReleaseCheck releases, LocalClock clock)
    {
        if (releases.Failed || releases.CheckedUtc is null)
        {
            return releases.CheckedUtc is { } last && releases.Releases.Count > 0
                ? $"Couldn't check for updates. Last checked {Day(last, clock)} at {Time(last, clock)}."
                : releases.Failed ? "Couldn't check for updates." : null;
        }

        return $"Checked {Day(releases.CheckedUtc.Value, clock)} at {Time(releases.CheckedUtc.Value, clock)}.";
    }

    private static string Day(DateTime utc, LocalClock clock)
    {
        var local = clock.ToLocal(utc).Date;
        var today = clock.Now.Date;
        return (today - local).Days switch
        {
            0 => "today",
            1 => "yesterday",
            _ => local.ToString("d MMMM", CultureInfo.InvariantCulture),
        };
    }

    private static string Time(DateTime utc, LocalClock clock)
    {
        return clock.ToLocal(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
