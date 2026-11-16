using System.Globalization;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

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
    /// <summary>The box for <paramref name="snapshot"/>, at <paramref name="clock"/>'s time, in the app's language.</summary>
    public static UpdateBoxView For(UpdateSnapshot snapshot, LocalClock clock, IStringLocalizer<Texts> texts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(texts);
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
        var checkedLine = Checked(snapshot.Releases, clock, texts);
        var canUpdate = offered is not null && !noHelper;

        // Asked for, not taken by the helper yet: the first step.
        if (snapshot.Pending is { } pending)
        {
            return Empty(UpdateBoxKind.Running, running, offered, [], null, noHelper) with
            {
                Steps = StepsFor(UpdateSteps.Download, pending.To, texts),
                From = running,
                To = pending.To,
                CanCancel = true,
                StartedLine = texts["Updates.StartedBy", pending.By, Time(pending.AtUtc, clock)],
            };
        }

        if (snapshot.Status is { } status && now - status.ChangedUtc <= UpdateService.ResultShownFor)
        {
            var stale = UpdateService.IsStale(status, now);
            if (status.IsRunning && !stale)
            {
                var to = status.To ?? offered ?? texts["Updates.NewVersion"];
                return Empty(UpdateBoxKind.Running, running, offered, [], null, noHelper) with
                {
                    Steps = StepsFor(status.Step ?? UpdateSteps.Download, to, texts),
                    From = status.From ?? running,
                    To = to,
                    CanCancel = status.Step is UpdateSteps.Download or UpdateSteps.Verify or UpdateSteps.Backup or UpdateSteps.Waiting,
                    StartedLine = snapshot.StartedBy is { } by
                        ? texts["Updates.StartedBy", by, Time(status.StartedUtc, clock)]
                        : texts["Updates.Started", Time(status.StartedUtc, clock)],
                };
            }

            if (ResultFor(status, stale, offered, snapshot.StartedBy, clock, texts) is { } result)
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
                    texts["Updates.NoAnswer"],
                    texts["Updates.NoAnswer.Text"],
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
    private static UpdateResultView? ResultFor(HelperStatus status, bool stale, string? offered, string? by, LocalClock clock, IStringLocalizer<Texts> texts)
    {
        string to = status.To ?? offered ?? texts["Updates.NewVersion"];
        if (stale)
        {
            return Failure(texts["Updates.Stale", to], texts["Updates.NothingChanged.TryAgain"]);
        }

        var at = Time(status.ChangedUtc, clock);
        return status.State switch
        {
            UpdateStates.Done => new UpdateResultView(texts["Updates.Done", to], by is null ? texts["Updates.Done.At", at] : texts["Updates.Done.AtBy", at, by], IsAlert: false, IsDone: true),
            UpdateStates.WentBack => Failure(
                texts["Updates.WentBack", to],
                texts["Updates.WentBack.Text", status.From ?? texts["Updates.VersionBefore"], Time(status.BackUtc ?? status.ChangedUtc, clock)]),
            UpdateStates.Failed => status.Reason switch
            {
                UpdateReasons.Download => Failure(texts["Updates.Download", to], texts["Updates.Download.Text"]),
                UpdateReasons.Signature => Failure(Capital(texts["Updates.Signature", to]), texts["Updates.Signature.Text"]),
                UpdateReasons.Space => Failure(texts["Updates.Space", to], Room(status, texts)),
                UpdateReasons.Backup => Failure(texts["Updates.Backup"], texts["Updates.Backup.Text"]),
                UpdateReasons.Cancelled => new UpdateResultView(texts["Updates.Cancelled"], texts["Updates.NothingChanged"], IsAlert: false, IsDone: false),
                UpdateReasons.Switching => new UpdateResultView(
                    texts["Updates.Switching"],
                    texts["Updates.Switching.Text"],
                    IsAlert: false,
                    IsDone: false),
                UpdateReasons.Interrupted => Failure(texts["Updates.Interrupted", to], texts["Updates.NothingChanged.TryAgain"]),
                _ => Failure(texts["Updates.Failed", to], texts["Updates.NothingChanged.TryAgain"]),
            },
            // The helper found nothing newer than what runs, while GitHub already lists a newer version.
            UpdateStates.Current when offered is not null => new UpdateResultView(texts["Updates.NotYet", offered], texts["Updates.NothingChanged.Later"], IsAlert: false, IsDone: false),
            _ => null,
        };
    }

    private static UpdateResultView Failure(string title, string text)
    {
        return new UpdateResultView(title, text, IsAlert: true, IsDone: false);
    }

    private static string Room(HelperStatus status, IStringLocalizer<Texts> texts)
    {
        return status is { NeedMegabytes: { } need, FreeMegabytes: { } free }
            ? texts["Updates.Space.Needs", texts["Updates.Gigabytes", need / 1024.0], texts["Updates.Gigabytes", free / 1024.0]]
            : texts["Updates.Space.Text"];
    }

    // A sentence that starts with a version, or with "the new version": a capital first letter.
    private static string Capital(string text)
    {
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] : text;
    }

    // The steps, each done, under way or still to come.
    private static IReadOnlyList<UpdateStepView> StepsFor(string current, string to, IStringLocalizer<Texts> texts)
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
                UpdateSteps.Download => texts["Updates.Step.Download", to].Value,
                UpdateSteps.Verify => texts["Updates.Step.Verify"],
                // Just before the restart, while a group is starting or running on.
                UpdateSteps.Backup when current == UpdateSteps.Waiting => texts["Updates.Step.Waiting"],
                UpdateSteps.Backup => texts["Updates.Step.Backup"],
                UpdateSteps.Restart => texts["Updates.Step.Restart"],
                _ => texts["Updates.Step.Check"],
            },
            i < at ? UpdateStepState.Done : i == at ? UpdateStepState.Now : UpdateStepState.Later))];
    }

    // "Checked today at 06:12.", or that it couldn't be checked.
    private static string? Checked(ReleaseCheck releases, LocalClock clock, IStringLocalizer<Texts> texts)
    {
        if (releases.Failed || releases.CheckedUtc is null)
        {
            return releases.CheckedUtc is { } last && releases.Releases.Count > 0
                ? texts["Updates.CheckFailedSince", Day(last, clock, texts), Time(last, clock)].Value
                : releases.Failed ? texts["Updates.CheckFailed"].Value : null;
        }

        return texts["Updates.Checked", Day(releases.CheckedUtc.Value, clock, texts), Time(releases.CheckedUtc.Value, clock)];
    }

    private static string Day(DateTime utc, LocalClock clock, IStringLocalizer<Texts> texts)
    {
        var local = clock.ToLocal(utc).Date;
        var today = clock.Now.Date;
        return (today - local).Days switch
        {
            0 => texts["Updates.Today"],
            1 => texts["Updates.Yesterday"],
            _ => local.ToString(texts["Updates.DayFormat"], CultureInfo.CurrentCulture),
        };
    }

    // Always 24 hours with ":", in every language.
    private static string Time(DateTime utc, LocalClock clock)
    {
        return clock.ToLocal(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
