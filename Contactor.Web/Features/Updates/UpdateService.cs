using System.Text.Json;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Updates;

/// <summary>An update asked for in Settings that the helper hasn't taken yet.</summary>
/// <param name="AtUtc">When it was asked for.</param>
/// <param name="By">Who asked, by name.</param>
/// <param name="To">The version offered then.</param>
/// <param name="EarlierId">The status's id then, so a new status can be told from it.</param>
public sealed record PendingUpdate(DateTime AtUtc, string By, string To, string? EarlierId);

/// <summary>The releases as last read from GitHub.</summary>
/// <param name="CheckedUtc">When they were last read; null before the first time.</param>
/// <param name="Releases">Every published release, newest first.</param>
/// <param name="Failed">The last try couldn't read them, e.g. GitHub couldn't be reached.</param>
public sealed record ReleaseCheck(DateTime? CheckedUtc, IReadOnlyList<Release> Releases, bool Failed)
{
    public static ReleaseCheck None { get; } = new(null, [], false);
}

/// <summary>Everything the Updates box shows, at one moment.</summary>
/// <param name="Build">The running app.</param>
/// <param name="HelperInstalled">Whether the update helper is on this machine (it wrote <c>updates/helper</c>).</param>
/// <param name="Releases">The releases from GitHub.</param>
/// <param name="Status">The helper's last status; null when there is none.</param>
/// <param name="StartedBy">Who asked for the update <see cref="Status"/> is about, by name; null when unknown.</param>
/// <param name="Pending">An update asked for that the helper hasn't taken yet.</param>
/// <param name="NoAnswer">The helper didn't take the last request.</param>
public sealed record UpdateSnapshot(
    AppBuild Build,
    bool HelperInstalled,
    ReleaseCheck Releases,
    HelperStatus? Status,
    string? StartedBy,
    PendingUpdate? Pending,
    bool NoAnswer)
{
    /// <summary>The releases newer than the running one, newest first; none for a development build.</summary>
    public IReadOnlyList<Release> Newer =>
        ReleaseVersion.TryParse(Build.Version, out var running)
            ? [.. Releases.Releases.Where(r => ReleaseVersion.TryParse(r.Version, out var v) && v > running)]
            : [];
}

/// <summary>
/// Updates from Settings ("Update now"), with the update helper on the host doing the work (see
/// <see cref="UpdateFolder"/> and install/contactor-updater.sh). It reads GitHub's releases once a day, asks the helper
/// for an update when someone presses "Update now" (never on its own), follows the helper's status, tells every open
/// page when the app is about to restart (<see cref="Changed"/>, <see cref="IsRestarting"/>), and puts each update in
/// the event log: who asked for which version, and how it ended.
/// </summary>
public sealed partial class UpdateService : BackgroundService
{
    /// <summary>How often GitHub's releases are read.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    /// <summary>After a failed read, the next try.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);

    /// <summary>How long the helper has to take a request; a request it didn't take is removed, never left for later.</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A status that says the update runs but hasn't changed for this long is from a helper that stopped (it has 45
    /// minutes at most): it shows as an update that didn't finish, and the next one may start.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(60);

    /// <summary>How long a finished update's result shows in the box.</summary>
    public static readonly TimeSpan ResultShownFor = TimeSpan.FromDays(1);

    /// <summary>How often the folder is looked at while an update runs or waits, and otherwise.</summary>
    public static readonly TimeSpan PollBusy = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PollIdle = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);

    private readonly UpdateFolder _folder;
    private readonly IReleaseSource _source;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly AppBuild _build;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();

    private ReleaseCheck _releases = ReleaseCheck.None;
    private HelperStatus? _status;
    private DateTime? _statusFileUtc;
    private string? _startedBy;
    private PendingUpdate? _pending;
    private bool _noAnswer;
    private bool _helperInstalled;
    private string? _recordedId;

    public UpdateService(
        UpdateFolder folder,
        IReleaseSource source,
        IDbContextFactory<ContactorDbContext> dbFactory,
        AppBuild build,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger)
    {
        _folder = folder;
        _source = source;
        _dbFactory = dbFactory;
        _build = build;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Something in the box changed; raised on the service's own thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The folder shared with the helper.</summary>
    public UpdateFolder Folder => _folder;

    /// <summary>Whether an update is restarting the app right now: every open page shows the splash.</summary>
    public bool IsRestarting
    {
        get
        {
            var status = Snapshot().Status;
            return status is { IsRunning: true } && UpdateSteps.IsRestarting(status.Step) && !IsStale(status);
        }
    }

    public UpdateSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new UpdateSnapshot(_build, _helperInstalled, _releases, _status, _startedBy, _pending, _noAnswer);
        }
    }

    /// <summary>Whether a running update's status hasn't changed for <see cref="StaleAfter"/>.</summary>
    public bool IsStale(HelperStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsRunning && _timeProvider.GetUtcNow().UtcDateTime - status.ChangedUtc > StaleAfter;
    }

    /// <summary>
    /// "Update now": asks the helper to update to the newest release, for <paramref name="user"/>. Returns why not, or
    /// null when it was asked. Refused while an update runs or waits (the box shows it instead), while a group is
    /// starting or running on, and where there is no helper or nothing newer.
    /// </summary>
    public async Task<string?> RequestAsync(SignedInUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot();
            if (!_build.IsRelease)
            {
                return "This is a development version: it isn't updated from here.";
            }

            if (!snapshot.HelperInstalled)
            {
                return "Contactor can't update itself on this machine.";
            }

            if (snapshot.Pending is not null || snapshot.Status is { IsRunning: true } status && !IsStale(status))
            {
                return "An update is already under way.";
            }

            if (snapshot.Newer is not [var newest, ..])
            {
                return "You have the newest version.";
            }

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            if (await db.GroupRuntimes.AnyAsync(r => r.Phase != GroupPhases.Idle, cancellationToken))
            {
                return "A group is starting or running on. Update once it's done.";
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                _folder.WriteRequest(UpdateFolder.UpdateWord);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogRequestFailed(ex);
                return "Couldn't ask for the update. Try again in a while.";
            }

            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                Actor = user.Email,
                Action = EventActions.UpdateRequested,
                Details = JsonSerializer.Serialize(new { from = _build.Version, to = newest.Version }),
            });
            await db.SaveChangesAsync(cancellationToken);
            LogRequested(user.Email, _build.Version, newest.Version);
            lock (_lock)
            {
                _pending = new PendingUpdate(now, user.DisplayName, newest.Version, _status?.Id);
                _noAnswer = false;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>"Cancel": until the restart. A request the helper hasn't taken is taken back; one under way is told to stop.</summary>
    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot();
            if (snapshot.Pending is not null && _folder.RequestWaiting)
            {
                _folder.DeleteRequest();
                lock (_lock)
                {
                    _pending = null;
                }
            }
            else if (snapshot.Status is { IsRunning: true, Step: UpdateSteps.Download or UpdateSteps.Verify or UpdateSteps.Backup })
            {
                try
                {
                    _folder.WriteRequest(UpdateFolder.CancelWord);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogRequestFailed(ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads GitHub's releases now, keeping the last ones when that fails.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        ReleaseCheck check;
        try
        {
            check = new ReleaseCheck(now, await _source.GetAsync(cancellationToken), false);
            SaveCache(check);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogCheckFailed(ex.Message);
            lock (_lock)
            {
                check = _releases with { Failed = true };
            }
        }

        lock (_lock)
        {
            _releases = check;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Looks at the folder once: the helper's status and whether it is there, and a request it didn't take. Raises
    /// <see cref="Changed"/> and returns true when something changed.
    /// </summary>
    public bool Poll()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var helper = _folder.HelperInstalled;
        var fileTime = _folder.StatusChangedUtc();
        var changed = false;
        lock (_lock)
        {
            if (helper != _helperInstalled)
            {
                _helperInstalled = helper;
                changed = true;
            }

            if (fileTime != _statusFileUtc)
            {
                _statusFileUtc = fileTime;
                var status = fileTime is null ? null : _folder.ReadStatus();
                if (status != _status)
                {
                    _status = status;
                    changed = true;
                }
            }

            if (_pending is { } pending)
            {
                if (_status is { } taken && taken.Id != pending.EarlierId)
                {
                    // The helper took it: from here its status tells.
                    _pending = null;
                    changed = true;
                }
                else if (now - pending.AtUtc > AnswerWithin * 2 || now - pending.AtUtc > AnswerWithin && _folder.RequestWaiting)
                {
                    // Never left for later: an update that starts by itself much later would surprise everyone.
                    _folder.DeleteRequest();
                    _pending = null;
                    _noAnswer = true;
                    changed = true;
                    LogNoAnswer();
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadSavedReleases();
        Poll();
        await FollowStatusAsync(stoppingToken);
        var nextCheck = DueCheck();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_timeProvider.GetUtcNow().UtcDateTime >= nextCheck)
                {
                    await CheckAsync(stoppingToken);
                    nextCheck = DueCheck();
                }

                if (Poll())
                {
                    await FollowStatusAsync(stoppingToken);
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                var snapshot = Snapshot();
                var busy = snapshot.Pending is not null || snapshot.Status is { IsRunning: true };
                await Task.Delay(busy ? PollBusy : PollIdle, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failure here must not stop the box from following the next update; it is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
                await Task.Delay(PollIdle, _timeProvider, stoppingToken);
            }
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    // When GitHub is read next: a day after the last read, an hour after a failed one, now when never.
    private DateTime DueCheck()
    {
        var releases = Snapshot().Releases;
        return releases.CheckedUtc is not { } checkedUtc ? DateTime.MinValue
            : releases.Failed ? _timeProvider.GetUtcNow().UtcDateTime + RetryEvery
            : checkedUtc + CheckEvery;
    }

    /// <summary>
    /// For a new status: who asked for the update it is about (from the event log), and, once it has ended, its event,
    /// once per update.
    /// </summary>
    public async Task FollowStatusAsync(CancellationToken cancellationToken)
    {
        var status = Snapshot().Status;
        if (status is null)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var from = status.StartedUtc.AddMinutes(-5);
        var until = status.StartedUtc.AddMinutes(1);
        var asked = await db.EventLog.AsNoTracking()
            .Where(e => e.Action == EventActions.UpdateRequested && e.TimestampUtc >= from && e.TimestampUtc <= until)
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e => e.Actor)
            .FirstOrDefaultAsync(cancellationToken);
        var by = asked is null ? null : await db.Users.AsNoTracking().Where(u => u.Email == asked).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken) ?? asked;
        lock (_lock)
        {
            if (_status == status)
            {
                _startedBy = by;
            }
        }

        var action = status.State switch
        {
            UpdateStates.Done => EventActions.UpdateDone,
            UpdateStates.WentBack => EventActions.UpdateWentBack,
            UpdateStates.Failed => EventActions.UpdateFailed,
            _ => null,
        };
        if (action is null || _recordedId == status.Id)
        {
            return;
        }

        var marker = $"\"id\":\"{status.Id}\"";
        var updateActions = new[] { EventActions.UpdateDone, EventActions.UpdateWentBack, EventActions.UpdateFailed };
        if (!await db.EventLog.AnyAsync(e => updateActions.Contains(e.Action) && e.Details != null && e.Details.Contains(marker), cancellationToken))
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = status.ChangedUtc,
                Actor = EventActors.System,
                Action = action,
                Details = JsonSerializer.Serialize(new { id = status.Id, from = status.From, to = status.To, reason = status.Reason }),
            });
            await db.SaveChangesAsync(cancellationToken);
            LogEnded(status.Id, status.State, status.From ?? "?", status.To ?? "?", status.Reason ?? "");
        }

        _recordedId = status.Id;
    }

    /// <summary>The releases last read from GitHub, as saved for the next start.</summary>
    public void LoadSavedReleases()
    {
        try
        {
            var info = new FileInfo(_folder.ReleasesPath);
            if (!info.Exists || info.Length > GitHubReleases.MaxResponseBytes)
            {
                return;
            }

            var cache = JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllBytes(info.FullName), CacheJson);
            if (cache?.CheckedUtc is { } checkedUtc && cache.Releases is { } releases)
            {
                var valid = releases
                    .Where(r => r is not null && ReleaseVersion.TryParse(r.Version, out _))
                    .Select(r => new Release(r.Version, r.Date, [.. (r.Changes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Take(30)]))
                    .ToList();
                lock (_lock)
                {
                    _releases = new ReleaseCheck(DateTime.SpecifyKind(checkedUtc, DateTimeKind.Utc), valid, false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Read again from GitHub.
        }
    }

    private void SaveCache(ReleaseCheck check)
    {
        try
        {
            Directory.CreateDirectory(_folder.Path);
            var partial = _folder.ReleasesPath + ".partial";
            File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(new ReleaseCache(check.CheckedUtc, [.. check.Releases]), CacheJson));
            File.Move(partial, _folder.ReleasesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience: the next start reads GitHub again.
        }
    }

    private sealed record ReleaseCache(DateTime? CheckedUtc, List<Release>? Releases);

    [LoggerMessage(Level = LogLevel.Information, Message = "{User} asked for an update from {From} to {To}")]
    private partial void LogRequested(string user, string from, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Id} ended: {State}, from {From} to {To} {Reason}")]
    private partial void LogEnded(string id, string state, string from, string to, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The update helper didn't take the request; it was removed")]
    private partial void LogNoAnswer();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the update request")]
    private partial void LogRequestFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read the releases from GitHub: {Reason}")]
    private partial void LogCheckFailed(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Following the update failed")]
    private partial void LogFailed(Exception exception);
}
