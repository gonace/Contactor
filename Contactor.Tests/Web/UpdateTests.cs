using System.Net;
using System.Text;
using System.Text.Json;
using Contactor.Web;
using Contactor.Web.Features.Backup;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Updates;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>The update helper's status file, as the app reads it.</summary>
public sealed class HelperStatusTests
{
    private const string Running = "helper=1\nid=20261115-140200\nstate=running\nstep=backup\nfrom=2026.11.14\nto=2026.11.20\nstarted=2026-11-15T13:02:00Z\nchanged=2026-11-15T13:02:40Z\n";

    [Fact]
    public void Should_ReadTheHelpersStatus()
    {
        var status = HelperStatus.Parse(Running);

        Assert.Equal(
            new HelperStatus("20261115-140200", UpdateStates.Running, UpdateSteps.Backup, "2026.11.14", "2026.11.20", null,
                new DateTime(2026, 11, 15, 13, 2, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 13, 2, 40, DateTimeKind.Utc)),
            status);
        Assert.Equal(DateTimeKind.Utc, status!.StartedUtc.Kind);
    }

    [Fact]
    public void Should_ReadWhyItFailed_AndTheRoomItNeeded()
    {
        var status = HelperStatus.Parse("id=20261115-140200\nstate=failed\nreason=space\nneed-mb=1300\nfree-mb=400\nfrom=2026.11.14\nto=\nstarted=2026-11-15T13:02:00Z\nchanged=2026-11-15T13:02:01Z\n");

        Assert.NotNull(status);
        Assert.Equal((UpdateReasons.Space, 1300, 400, null, (string?)null), (status.Reason, status.NeedMegabytes, status.FreeMegabytes, status.Step, status.To));
    }

    [Theory]
    [InlineData("")]
    [InlineData("state=running\nstarted=2026-11-15T13:02:00Z\nchanged=2026-11-15T13:02:00Z")]
    [InlineData("id=../../etc/passwd\nstate=running\nstarted=2026-11-15T13:02:00Z\nchanged=2026-11-15T13:02:00Z")]
    [InlineData("id=20261115-140200\nstate=exploded\nstarted=2026-11-15T13:02:00Z\nchanged=2026-11-15T13:02:00Z")]
    [InlineData("id=20261115-140200\nstate=done\nstarted=yesterday\nchanged=2026-11-15T13:02:00Z")]
    [InlineData("id=20261115-140200\nstate=done\nstarted=2026-11-15T13:02:00Z")]
    public void Should_LeaveOutWhatIsntAStatus(string text)
    {
        Assert.Null(HelperStatus.Parse(text));
    }

    [Fact]
    public void Should_LeaveOutAStatusTooBigToBeTheHelpers()
    {
        Assert.Null(HelperStatus.Parse(Running + new string('x', HelperStatus.MaxBytes)));
    }

    // The folder is the app's own: whatever else is in the file never reaches the page.
    [Fact]
    public void Should_LeaveOutValuesOfTheWrongForm()
    {
        var status = HelperStatus.Parse(Running.Replace("step=backup", "step=rm -rf /", StringComparison.Ordinal)
            .Replace("to=2026.11.20", "to=<script>", StringComparison.Ordinal) + "reason=whatever\nneed-mb=-5\n");

        Assert.NotNull(status);
        Assert.Equal((null, null, null, (int?)null), (status.Step, status.To, status.Reason, status.NeedMegabytes));
    }

    [Fact]
    public void Should_HaveNoStep_OnceItEnded()
    {
        var status = HelperStatus.Parse(Running.Replace("state=running", "state=done", StringComparison.Ordinal));

        Assert.Null(status!.Step);
        Assert.False(status.IsRunning);
    }
}

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("2026.11.20", "2026.11.4")]
    [InlineData("2026.11.4.1", "2026.11.4")]
    [InlineData("2027.1.1", "2026.12.31.9")]
    [InlineData("v2026.11.20", "2026.11.14")]
    public void Should_TellTheNewer(string newer, string older)
    {
        Assert.True(ReleaseVersion.TryParse(newer, out var a));
        Assert.True(ReleaseVersion.TryParse(older, out var b));
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact]
    public void Should_SeeTheSameVersion_WithOrWithoutTheTagsV()
    {
        Assert.True(ReleaseVersion.TryParse("v2026.11.20", out var tag));
        Assert.True(ReleaseVersion.TryParse("2026.11.20", out var plain));
        Assert.Equal(plain, tag);
        Assert.Equal("2026.11.20", tag.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0-dev")]
    [InlineData("2026.11")]
    [InlineData("2026.11.20.1.2")]
    [InlineData("2026..20")]
    [InlineData("2026.11.-1")]
    [InlineData("latest")]
    [InlineData("2026.11.123456")]
    public void Should_RefuseWhatIsntARelease(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }
}

/// <summary>The releases from GitHub's API, and what's new in each.</summary>
public sealed class GitHubReleasesTests
{
    [Fact]
    public void Should_ListPublishedReleases_NewestFirst()
    {
        var releases = GitHubReleases.Parse(Json(
            Item("v2026.11.17", "2026-11-17T09:00:00Z", "## What's Changed\n* The test run prints only its own lines by @gonace in https://github.com/gonace/Contactor/pull/290\n\n**Full Changelog**: https://github.com/gonace/Contactor/compare/v2026.11.14...v2026.11.17"),
            Item("v2026.11.20", "2026-11-20T07:30:00Z", "* Several relay boards in one app by @gonace in https://github.com/gonace/Contactor/pull/305\r\n* Events and Debug name the board by @gonace in https://github.com/gonace/Contactor/pull/306\r\n"),
            Item("v2026.11.14", "2026-11-14T07:30:00Z", "")));

        Assert.Equal(["2026.11.20", "2026.11.17", "2026.11.14"], releases.Select(r => r.Version));
        Assert.Equal(["Several relay boards in one app", "Events and Debug name the board"], releases[0].Changes);
        Assert.Equal(["The test run prints only its own lines"], releases[1].Changes);
        Assert.Empty(releases[2].Changes);
        Assert.Equal(new DateOnly(2026, 11, 20), releases[0].Date);
    }

    [Fact]
    public void Should_LeaveOutDraftsPreReleasesAndOtherTags()
    {
        var releases = GitHubReleases.Parse(Json(
            Item("v2026.11.21", "2026-11-21T07:30:00Z", "* Not yet", draft: true),
            Item("v2026.11.22", "2026-11-22T07:30:00Z", "* Trying", prerelease: true),
            Item("demo-1", "2026-11-22T07:30:00Z", "* Demo"),
            Item("v2026.11.20", null, "* Real")));

        Assert.Equal("2026.11.20", Assert.Single(releases).Version);
        Assert.Null(releases[0].Date);
    }

    // The notes are made from the pull requests' titles, those labelled "internal" left out (.github/release.yml):
    // what is left besides the list is GitHub's own.
    [Fact]
    public void Should_KeepOnlyTheListedChanges()
    {
        var changes = GitHubReleases.Changes(
            "## What's Changed\n### Fixes\n* Fix the clock by @someone-else in https://github.com/gonace/Contactor/pull/9\n- A plain line\n\n## New Contributors\n* @someone-else made their first contribution in https://github.com/gonace/Contactor/pull/9\n\n**Full Changelog**: https://x");

        Assert.Equal(["Fix the clock", "A plain line"], changes);
    }

    // The Release workflow writes an "Images" section at the end of the notes (Issue #314): a table and docker pull
    // lines, never changes.
    [Fact]
    public void Should_LeaveOutTheImagesSection()
    {
        var changes = GitHubReleases.Changes(
            "## What's Changed\n* Fix the clock by @someone in https://github.com/gonace/Contactor/pull/9\n\n<!-- images -->\n## Images\n\n| Image | Version | Digest |\n|---|---|---|\n| [ghcr.io/gonace/contactor](https://github.com/gonace/Contactor/pkgs/container/contactor) | `2026.11.15` | `sha256:aa` |\n\n```sh\ndocker pull ghcr.io/gonace/contactor@sha256:aa\n```");

        Assert.Equal(["Fix the clock"], changes);
    }

    [Fact]
    public void Should_ShortenAVeryLongChange_AndListAtMostThirty()
    {
        var notes = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"* Change {i}")) + "\n* " + new string('a', 500);

        var changes = GitHubReleases.Changes(notes);

        Assert.Equal(30, changes.Count);
        Assert.True(GitHubReleases.Changes("* " + new string('a', 500))[0].Length <= 200);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"releases\"")]
    [InlineData("[{")]
    public void Should_Throw_OnWhatIsntAListOfReleases(string json)
    {
        Assert.ThrowsAny<JsonException>(() => GitHubReleases.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Should_SkipItemsThatArentReleases()
    {
        Assert.Equal("2026.11.20", Assert.Single(GitHubReleases.Parse(Encoding.UTF8.GetBytes("[1, \"x\", null, {\"tag_name\": 5}, {\"tag_name\": \"v2026.11.20\"}]"))).Version);
    }

    [Fact]
    public async Task Should_AskGitHubWithoutALogin_AndSayNothingAboutTheInstallation()
    {
        var handler = new RecordingHandler("[" + Item("v2026.11.20", "2026-11-20T07:30:00Z", "* New") + "]");
        var source = new GitHubReleases(new SingleClientFactory(handler), Options.Create(new UpdateOptions()));

        var releases = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2026.11.20", Assert.Single(releases).Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/gonace/Contactor/releases?per_page=30", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Contactor", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Should_ReadNothing_WithoutAnAddress()
    {
        var handler = new RecordingHandler("[]");
        var source = new GitHubReleases(new SingleClientFactory(handler), Options.Create(new UpdateOptions { ReleasesUrl = "" }));

        Assert.Empty(await source.GetAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Should_Throw_WhenGitHubAnswersWithAnError()
    {
        var source = new GitHubReleases(new SingleClientFactory(new RecordingHandler("rate limited", HttpStatusCode.Forbidden)), Options.Create(new UpdateOptions()));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_Throw_OnAnAnswerTooBigToBeGitHubs()
    {
        var source = new GitHubReleases(new SingleClientFactory(new RecordingHandler(new string(' ', GitHubReleases.MaxResponseBytes + 1))), Options.Create(new UpdateOptions()));

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetAsync(TestContext.Current.CancellationToken));
    }

    internal static string Item(string tag, string? published, string body, bool draft = false, bool prerelease = false)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["published_at"] = published,
            ["body"] = body,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
        });
    }

    private static byte[] Json(params string[] items)
    {
        return Encoding.UTF8.GetBytes("[" + string.Join(',', items) + "]");
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    internal sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}

/// <summary>An update's log: secrets stripped before it is shown or downloaded.</summary>
public sealed class UpdateLogTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("starting with Tunnel__Token=eyJhIjoiMTIzIn0.c2VjcmV0 now", "starting with Tunnel__Token=[hidden] now")]
    [InlineData("TS_AUTHKEY tskey-auth-kQ1w2E3r4T5y6U7i8O9p", "TS_AUTHKEY [hidden]")]
    [InlineData("Vm201__Password=hunter2, then", "Vm201__Password=[hidden], then")]
    [InlineData("\"password\": \"s3cr3t\"", "\"password\": \"[hidden]\"")]
    [InlineData("Authorization: Bearer abc.def", "Authorization: [hidden]")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: [hidden]")]
    [InlineData("api_key: AbCdEf", "api_key: [hidden]")]
    [InlineData("a random Ab3dEfGh1jKlMnOpQrStUvWx99z here", "a random [hidden] here")]
    [InlineData("a jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc", "a jwt [hidden]")]
    public void Should_HideSecrets(string line, string shown)
    {
        Assert.Equal(shown, UpdateLog.Strip(line));
    }

    [Theory]
    [InlineData("12:02:40 Downloaded 2026.11.20 (ghcr.io/gonace/contactor@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d).")]
    [InlineData("12:02:41 Backing up /opt/contactor/data/updates/before-update.db")]
    [InlineData("ConnectionStrings__Default is set; the board's key facts")]
    [InlineData("12:03:00 It wasn't healthy after 300 seconds.")]
    public void Should_KeepWhatIsntASecret(string line)
    {
        Assert.Equal(line, UpdateLog.Strip(line));
    }

    // The same lines as scripts/updater-test.sh checks in the helper's own log.
    [Fact]
    public void Should_StripEveryLine_OfAWholeLog()
    {
        var log = "11:20:38 The last lines of 2026.11.24's log:\n    starting with Tunnel__Token=eyJhIjoiMTIzNDU2Nzg5MCJ9.c2VjcmV0 and tskey-auth-kQ1w2E3r4T5y6U7i8O9p\n";

        var stripped = UpdateLog.Strip(log);

        Assert.Equal("11:20:38 The last lines of 2026.11.24's log:\n    starting with Tunnel__Token=[hidden] and [hidden]\n", stripped);
    }

    // Every pattern runs without backtracking, on lines cut to MaxLineLength: a huge line costs only its length.
    [Fact]
    public void Should_CutAHugeLine_AndStillHideWhatItKeeps()
    {
        var huge = "api_key " + new string('a', 1_000_000);

        var stripped = UpdateLog.Strip(huge);

        // The first cut drops the unfinished word, here all of the run.
        Assert.Equal("api_key …", stripped);
        Assert.Equal(UpdateLog.MaxLineLength + 1, UpdateLog.Strip(new string('a', UpdateLog.FirstCut)).Length);
        Assert.Equal("x password=[hidden] " + new string('b', UpdateLog.MaxLineLength - 20) + "…",
            UpdateLog.Strip("x password=hunter2 " + new string('b', 5000)));
        Assert.DoesNotContain("hunter2", UpdateLog.Strip(new string('c', 3000) + " password=hunter2"), StringComparison.Ordinal);
    }

    // A token across the cut is hidden whole: none of it is left showing before the "…".
    [Fact]
    public void Should_HideATokenAcrossTheCut()
    {
        var token = "Zq7RmW2xKp9LtVb4Nc8YhJ3sDf6GuE1aQw5";
        var line = new string('a', UpdateLog.MaxLineLength - 10) + " " + token + " after";

        var stripped = UpdateLog.Strip(line);

        Assert.Equal(new string('a', UpdateLog.MaxLineLength - 10) + " [hidden] …", stripped);
        Assert.DoesNotContain("Zq", stripped, StringComparison.Ordinal);
    }

    // The first cut drops the word it cuts through, a token's start included, and the line says it was cut, also when
    // what is left after hiding is shorter than MaxLineLength.
    [Fact]
    public void Should_DropATokenAcrossTheFirstCut()
    {
        var before = "x eyJ" + new string('A', 7000) + ".sig " + new string('a', 970) + " ";
        var line = before + "Zq7RmW2xKp9LtVb4Nc8YhJ3sDf6GuE1aQw5 after";
        Assert.True(before.Length < UpdateLog.FirstCut && line.Length > UpdateLog.FirstCut);

        var stripped = UpdateLog.Strip(line);

        Assert.Equal("x [hidden] " + new string('a', 970) + " …", stripped);
        Assert.DoesNotContain("Zq", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReadALog_ByItsUpdatesId_Stripped()
    {
        using var folder = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "update-20261115-140200.log"), "12:02 password=hunter2\n", Cancel);

        Assert.Equal("12:02 password=[hidden]\n", await UpdateLog.ReadAsync(folder.Path, "20261115-140200", Cancel));
        Assert.Null(await UpdateLog.ReadAsync(folder.Path, "20261115-140201", Cancel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../update-20261115-140200")]
    [InlineData("20261115-140200/../../x")]
    [InlineData("*")]
    public async Task Should_ReadNothingButALogByAnId(string? id)
    {
        using var folder = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "update-20261115-140200.log"), "x", Cancel);

        Assert.Null(await UpdateLog.ReadAsync(folder.Path, id, Cancel));
    }

    [Fact]
    public async Task Should_ShowOnlyTheEnd_OfAHugeLog()
    {
        using var folder = new TempFolder();
        var text = string.Concat(Enumerable.Range(0, 40_000).Select(i => $"line {i}\n"));
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "update-20261115-140200.log"), text, Cancel);

        var log = await UpdateLog.ReadAsync(folder.Path, "20261115-140200", Cancel);

        Assert.True(log!.Length <= UpdateLog.MaxBytes);
        Assert.EndsWith("line 39999\n", log, StringComparison.Ordinal);
        Assert.StartsWith("line ", log, StringComparison.Ordinal);
    }
}

/// <summary>What the Updates box shows, in each state.</summary>
public sealed class UpdateBoxViewTests
{
    private static readonly AppBuild Running = new("2026.11.14", "abc");
    private static readonly Release Newest = new("2026.11.20", new DateOnly(2026, 11, 20), ["Several relay boards in one app"]);
    private static readonly Release Between = new("2026.11.17", new DateOnly(2026, 11, 17), ["The test run prints only its own lines"]);
    private static readonly Release Quiet = new("2026.11.18", new DateOnly(2026, 11, 18), []);
    private static readonly Release Older = new("2026.11.10", new DateOnly(2026, 11, 10), ["Old news"]);

    private readonly ManualTimeProvider _time = new();

    // 10:00 UTC is 11:00 in Stockholm.
    private LocalClock Clock => new(TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"), _time);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact]
    public void Should_OfferTheNewestRelease_WithWhatsNewInEachNewerOne()
    {
        var view = View(Snapshot(releases: [Newest, Quiet, Between, Older]));

        Assert.Equal(UpdateBoxKind.Available, view.Kind);
        Assert.Equal(("2026.11.14", "2026.11.20", true), (view.Running, view.Offered, view.CanUpdate));
        // A release that lists no change (every change "internal") doesn't show under What's new.
        Assert.Equal(["2026.11.20", "2026.11.17"], view.WhatsNew.Select(r => r.Version));
    }

    [Fact]
    public void Should_SayItIsTheNewest_AndWhenItWasChecked()
    {
        var checkedAt = Now.AddHours(-4).AddMinutes(12);

        var view = View(Snapshot(releases: [Older], checkedUtc: checkedAt));

        Assert.Equal(UpdateBoxKind.Current, view.Kind);
        Assert.Null(view.Offered);
        Assert.Equal("Checked today at 07:12.", view.CheckedLine);
        Assert.Equal("Checked yesterday at 07:12.", View(Snapshot(releases: [Older], checkedUtc: checkedAt.AddDays(-1))).CheckedLine);
    }

    [Fact]
    public void Should_NameTheDate_WhenCheckedDaysAgo_InTheLanguagesFormat()
    {
        var checkedAt = Now.AddDays(-3).AddHours(-4).AddMinutes(12);

        Assert.Equal("Checked 9 November at 07:12.", View(Snapshot(releases: [Older], checkedUtc: checkedAt)).CheckedLine);
        using (TestCulture.Formats("sv-SE"))
        {
            Assert.Equal("Checked 9 november at 07:12.", View(Snapshot(releases: [Older], checkedUtc: checkedAt)).CheckedLine);
        }
    }

    [Fact]
    public void Should_SaySoWhenGitHubCantBeReached()
    {
        Assert.Equal("Couldn't check for updates.", View(Snapshot(releases: [], checkedUtc: null, failed: true)).CheckedLine);
        Assert.Equal("Couldn't check for updates. Last checked yesterday at 11:00.",
            View(Snapshot(releases: [Older], checkedUtc: Now.AddDays(-1), failed: true)).CheckedLine);
    }

    // The releases already read stay offered while GitHub can't be reached.
    [Fact]
    public void Should_KeepOfferingWhatWasRead_WhileGitHubCantBeReached()
    {
        var view = View(Snapshot(releases: [Newest], checkedUtc: Now.AddDays(-1), failed: true));

        Assert.Equal((UpdateBoxKind.Available, "2026.11.20"), (view.Kind, view.Offered));
    }

    [Fact]
    public void Should_NotOfferUpdateNow_WithoutTheHelper()
    {
        var view = View(Snapshot(releases: [Newest], helper: false));

        Assert.Equal((UpdateBoxKind.Available, "2026.11.20", false, true), (view.Kind, view.Offered, view.CanUpdate, view.NoHelper));
    }

    [Fact]
    public void Should_NotUpdateADevelopmentBuild()
    {
        var view = View(Snapshot(releases: [Newest]) with { Build = new AppBuild(AppBuild.Development, null) });

        Assert.Equal((UpdateBoxKind.Development, false), (view.Kind, view.CanUpdate));
    }

    [Fact]
    public void Should_ShowTheFirstStep_AsSoonAsItIsAskedFor()
    {
        var view = View(Snapshot(releases: [Newest]) with { Pending = new PendingUpdate(Now.AddSeconds(-2), "John", "2026.11.20", null) });

        Assert.Equal(UpdateBoxKind.Running, view.Kind);
        Assert.Equal([UpdateStepState.Now, UpdateStepState.Later, UpdateStepState.Later, UpdateStepState.Later, UpdateStepState.Later], view.Steps.Select(s => s.State));
        Assert.Equal("Downloading 2026.11.20", view.Steps[0].Text);
        Assert.Equal("Started by John at 10:59. The relays stay as they are.", view.StartedLine);
        Assert.True(view.CanCancel);
    }

    [Theory]
    [InlineData(UpdateSteps.Download, 0, true)]
    [InlineData(UpdateSteps.Verify, 1, true)]
    [InlineData(UpdateSteps.Backup, 2, true)]
    [InlineData(UpdateSteps.Restart, 3, false)]
    [InlineData(UpdateSteps.Check, 4, false)]
    public void Should_ShowEachStep_AndCancelOnlyUntilTheRestart(string step, int now, bool canCancel)
    {
        var view = View(Snapshot(releases: [Newest], status: Status(UpdateStates.Running, step), startedBy: "John"));

        Assert.Equal(UpdateBoxKind.Running, view.Kind);
        Assert.Equal(("2026.11.14", "2026.11.20"), (view.From, view.To));
        Assert.Equal(["Downloading 2026.11.20", "Checking that it's ours", "Backing up", "Restarting", "Checking the new version"], view.Steps.Select(s => s.Text));
        Assert.Equal(now, view.Steps.ToList().FindIndex(s => s.State == UpdateStepState.Now));
        Assert.All(view.Steps.Take(now), s => Assert.Equal(UpdateStepState.Done, s.State));
        Assert.Equal(canCancel, view.CanCancel);
        Assert.Equal("Started by John at 10:58. The relays stay as they are.", view.StartedLine);
    }

    [Fact]
    public void Should_SayItWasUpdated_AndByWhom()
    {
        var view = View(Snapshot(build: new AppBuild("2026.11.20", null), releases: [Newest], status: Status(UpdateStates.Done), startedBy: "John"));

        Assert.Equal((UpdateBoxKind.Result, null), (view.Kind, view.Offered));
        Assert.Equal(new UpdateResultView("Updated to 2026.11.20", "At 10:59, by John.", IsAlert: false, IsDone: true), view.Result);
        Assert.Null(view.LogId);
    }

    [Fact]
    public void Should_SayItWentBack_WithTryAgainAndTheLog()
    {
        var status = Status(UpdateStates.WentBack) with { Reason = UpdateReasons.Health, BackUtc = Now.AddMinutes(-1) };

        var view = View(Snapshot(releases: [Newest], status: status));

        Assert.Equal(new UpdateResultView("The update to 2026.11.20 didn't work", "Contactor went back to 2026.11.14 at 10:59. Nothing was lost.", IsAlert: true, IsDone: false), view.Result);
        Assert.True(view.CanUpdate);
        Assert.Equal("20261112-095800", view.LogId);
        Assert.Empty(view.WhatsNew);
    }

    [Theory]
    [InlineData(UpdateReasons.Download, "Couldn't download 2026.11.20", "Nothing was changed. Check the internet connection and try again.", true)]
    [InlineData(UpdateReasons.Signature, "2026.11.20 didn't pass the check that it's ours", "So it wasn't installed. Nothing was changed. Try again later.", true)]
    [InlineData(UpdateReasons.Space, "Not enough room for 2026.11.20", "Nothing was changed. The update needs 1.3 GB of free disk space, and there's 0.4 GB. Make room and try again.", true)]
    [InlineData(UpdateReasons.Backup, "Couldn't back up before updating", "Nothing was changed. Try again in a while.", true)]
    [InlineData(UpdateReasons.Interrupted, "The update to 2026.11.20 was interrupted", "Nothing was changed. Try again.", true)]
    [InlineData(UpdateReasons.Cancelled, "The update was cancelled", "Nothing was changed.", false)]
    [InlineData(UpdateReasons.Error, "The update to 2026.11.20 didn't start", "Nothing was changed. Try again.", true)]
    public void Should_SayWhyItDidntUpdate(string reason, string title, string text, bool alert)
    {
        var status = Status(UpdateStates.Failed) with { Reason = reason, NeedMegabytes = 1331, FreeMegabytes = 410 };

        var view = View(Snapshot(releases: [Newest], status: status));

        Assert.Equal(new UpdateResultView(title, text, alert, IsDone: false), view.Result);
        Assert.True(view.CanUpdate);
        Assert.Equal(alert ? status.Id : null, view.LogId);
    }

    [Fact]
    public void Should_ShowTheResultForADay_ThenTheBoxAsUsual()
    {
        var snapshot = Snapshot(releases: [Newest], status: Status(UpdateStates.Failed) with { Reason = UpdateReasons.Download });
        Assert.Equal(UpdateBoxKind.Result, View(snapshot).Kind);

        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(UpdateBoxKind.Available, View(snapshot).Kind);
    }

    // A helper that stopped halfway (its status still says running an hour later): not shown as running for ever.
    [Fact]
    public void Should_SayItDidntFinish_WhenTheStatusStoppedChanging()
    {
        var snapshot = Snapshot(releases: [Newest], status: Status(UpdateStates.Running, UpdateSteps.Download));

        _time.Advance(TimeSpan.FromMinutes(61));
        var view = View(snapshot);

        Assert.Equal(UpdateBoxKind.Result, view.Kind);
        Assert.Equal("The update to 2026.11.20 didn't finish", view.Result!.Title);
        Assert.True(view.CanUpdate);
    }

    [Fact]
    public void Should_SayItWaitsForTheRelays_WhileAGroupIsSwitching()
    {
        var snapshot = Snapshot(releases: [Newest], status: Status(UpdateStates.Running, UpdateSteps.Waiting), startedBy: "John");

        var view = View(snapshot);

        Assert.Equal(UpdateBoxKind.Running, view.Kind);
        Assert.Equal(["Downloading 2026.11.20", "Checking that it's ours", "Waiting for the relays to finish switching", "Restarting", "Checking the new version"], view.Steps.Select(s => s.Text));
        Assert.Equal([UpdateStepState.Done, UpdateStepState.Done, UpdateStepState.Now, UpdateStepState.Later, UpdateStepState.Later], view.Steps.Select(s => s.State));
        Assert.True(view.CanCancel);

        // It waits 10 minutes at most, without a new status meanwhile: not a helper that stopped.
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(UpdateBoxKind.Running, View(snapshot).Kind);
    }

    [Fact]
    public void Should_SayTheUpdateWasStopped_WhenTheRelaysKeptSwitching()
    {
        var view = View(Snapshot(releases: [Newest], status: Status(UpdateStates.Failed) with { Reason = UpdateReasons.Switching }));

        Assert.Equal(new UpdateResultView("The update was stopped because the relays were still switching", "Try again later. Nothing was changed.", IsAlert: false, IsDone: false), view.Result);
        Assert.True(view.CanUpdate);
    }

    // A helper that stopped while the app restarted: the splash and the steps don't stay for an hour.
    [Fact]
    public void Should_SayItDidntFinish_SoonerWhenItStoppedDuringTheRestart()
    {
        var snapshot = Snapshot(releases: [Newest], status: Status(UpdateStates.Running, UpdateSteps.Check));

        _time.Advance(TimeSpan.FromMinutes(13));
        Assert.Equal(UpdateBoxKind.Running, View(snapshot).Kind);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("The update to 2026.11.20 didn't finish", View(snapshot).Result!.Title);
    }

    [Fact]
    public void Should_SaySoWhenTheHelperDidntAnswer()
    {
        var view = View(Snapshot(releases: [Newest]) with { NoAnswer = true });

        Assert.Equal(UpdateBoxKind.Result, view.Kind);
        Assert.Equal("The update didn't start", view.Result!.Title);
        Assert.Contains("Run the install command again", view.Result.Text, StringComparison.Ordinal);
    }

    // GitHub lists a release whose image the helper didn't find newer yet.
    [Fact]
    public void Should_SayTheReleaseCantBeDownloadedYet()
    {
        var view = View(Snapshot(releases: [Newest], status: Status(UpdateStates.Current) with { To = "2026.11.14" }));

        Assert.Equal(new UpdateResultView("2026.11.20 can't be downloaded yet", "Nothing was changed. Try again later.", IsAlert: false, IsDone: false), view.Result);
    }

    private UpdateBoxView View(UpdateSnapshot snapshot) => UpdateBoxView.For(snapshot, Clock, TestTexts.For<Contactor.Web.Resources.UpdatesTexts>());

    private UpdateSnapshot Snapshot(
        IReadOnlyList<Release> releases,
        AppBuild? build = null,
        bool helper = true,
        DateTime? checkedUtc = default,
        bool failed = false,
        HelperStatus? status = null,
        string? startedBy = null)
    {
        return new UpdateSnapshot(build ?? Running, helper, new ReleaseCheck(checkedUtc ?? Now, releases, failed), status, startedBy, null, false);
    }

    private HelperStatus Status(string state, string? step = null)
    {
        return new HelperStatus("20261112-095800", state, step, "2026.11.14", "2026.11.20", null, Now.AddMinutes(-2), Now.AddMinutes(-1));
    }
}

/// <summary>The service between Settings and the update helper: the request, the helper's answer and the events.</summary>
public sealed class UpdateServiceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly SignedInUser John = new("john@example.com", "John");

    [Fact]
    public async Task Should_AskTheHelper_WithOneWord_AndLogWhoAskedForWhichVersion()
    {
        await using var rig = await Rig.StartAsync();

        Assert.Null(await rig.Service.RequestAsync(John, Cancel));

        Assert.Equal("update\n", await File.ReadAllTextAsync(rig.Folder.RequestPath, Cancel));
        var asked = Assert.Single(await rig.EventsAsync());
        Assert.Equal(("john@example.com", EventActions.UpdateRequested), (asked.Actor, asked.Action));
        Assert.Equal("{\"from\":\"2026.11.14\",\"to\":\"2026.11.20\"}", asked.Details);
        Assert.Equal("John", rig.Service.Snapshot().Pending!.By);
    }

    // "Update now" pressed twice: the second shows the first one's progress rather than starting another.
    [Fact]
    public async Task Should_RefuseASecondUpdate_WhileOneWaitsOrRuns()
    {
        await using var rig = await Rig.StartAsync();
        Assert.Null(await rig.Service.RequestAsync(John, Cancel));

        Assert.Equal("An update is already under way.", await rig.Service.RequestAsync(John, Cancel));

        rig.Folder.DeleteRequest();
        rig.WriteStatus("state=running\nstep=download");
        rig.Service.Poll();
        Assert.Equal("An update is already under way.", await rig.Service.RequestAsync(John, Cancel));
        Assert.Single(await rig.EventsAsync());
    }

    [Fact]
    public async Task Should_RefuseAnUpdate_WhileAGroupIsStartingOrRunningOn()
    {
        await using var rig = await Rig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = new Board { SortOrder = 1 };
            db.Boards.Add(board);
            await db.SaveChangesAsync(Cancel);
            var group = new RelayGroup { Name = "Fan heater", BoardId = board.Id };
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id, Phase = GroupPhases.Stopping });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Equal("A group is starting or running on. Update once it's done.", await rig.Service.RequestAsync(John, Cancel));
        Assert.False(File.Exists(rig.Folder.RequestPath));
        Assert.Empty(await rig.EventsAsync());
    }

    [Fact]
    public async Task Should_RefuseAnUpdate_WithoutTheHelper_OrANewerVersion_OrOnADevelopmentBuild()
    {
        await using (var noHelper = await Rig.StartAsync(helper: false))
        {
            Assert.Equal("Contactor can't update itself on this machine.", await noHelper.Service.RequestAsync(John, Cancel));
        }

        await using (var newest = await Rig.StartAsync(releases: []))
        {
            Assert.Equal("You have the newest version.", await newest.Service.RequestAsync(John, Cancel));
        }

        await using var development = await Rig.StartAsync(build: new AppBuild(AppBuild.Development, null));
        Assert.Equal("This is a development version: it isn't updated from here.", await development.Service.RequestAsync(John, Cancel));
        Assert.False(File.Exists(development.Folder.RequestPath));
    }

    // Nothing is left for later: a request the helper doesn't take is removed, never acted on hours later.
    [Fact]
    public async Task Should_TakeBackARequestTheHelperDidntTake()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Service.RequestAsync(John, Cancel);

        rig.Time.Advance(TimeSpan.FromSeconds(29));
        rig.Service.Poll();
        Assert.True(File.Exists(rig.Folder.RequestPath));

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(rig.Service.Poll());

        Assert.False(File.Exists(rig.Folder.RequestPath));
        Assert.True(rig.Service.Snapshot().NoAnswer);
        Assert.Null(rig.Service.Snapshot().Pending);
    }

    [Fact]
    public async Task Should_FollowTheHelper_OnceItTookTheRequest()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Service.RequestAsync(John, Cancel);
        rig.Folder.DeleteRequest();

        rig.WriteStatus("state=running\nstep=backup");
        Assert.True(rig.Service.Poll());
        await rig.Service.FollowStatusAsync(Cancel);

        var snapshot = rig.Service.Snapshot();
        Assert.Null(snapshot.Pending);
        Assert.Equal((UpdateSteps.Backup, "John"), (snapshot.Status!.Step, snapshot.StartedBy));
        Assert.False(rig.Service.IsRestarting);
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(rig.Service.Poll());
        Assert.False(snapshot.NoAnswer);
    }

    [Theory]
    [InlineData(UpdateSteps.Backup, false)]
    [InlineData(UpdateSteps.Restart, true)]
    [InlineData(UpdateSteps.Check, true)]
    [InlineData(UpdateSteps.Back, true)]
    public async Task Should_ShowTheSplash_FromTheRestart(string step, bool restarting)
    {
        await using var rig = await Rig.StartAsync();

        rig.WriteStatus($"state=running\nstep={step}");
        rig.Service.Poll();

        Assert.Equal(restarting, rig.Service.IsRestarting);
    }

    // Nothing in the folder changes when the helper stops: the service's own polling tells the pages.
    [Theory]
    [InlineData(UpdateSteps.Restart)]
    [InlineData(UpdateSteps.Check)]
    [InlineData(UpdateSteps.Back)]
    public async Task Should_TakeTheSplashAway_ForAHelperThatStopped(string step)
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteStatus($"state=running\nstep={step}");
        rig.Service.Poll();
        Assert.True(rig.Service.IsRestarting);
        var changes = 0;
        rig.Service.Changed += (_, _) => changes++;

        rig.Time.Advance(UpdateService.RestartStaleAfter);
        Assert.False(rig.Service.Poll());
        Assert.True(rig.Service.IsRestarting);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(rig.Service.Poll());

        Assert.Equal(1, changes);
        Assert.False(rig.Service.IsRestarting);
        Assert.Null(await rig.Service.RequestAsync(John, Cancel));
    }

    [Fact]
    public async Task Should_SayTheUpdateCantBeAskedFor_WhenTheDatabaseFails()
    {
        await using var rig = await Rig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"EventLog\"", Cancel);
        }

        Assert.Equal("Couldn't ask for the update. Try again in a while.", await rig.Service.RequestAsync(John, Cancel));

        Assert.False(File.Exists(rig.Folder.RequestPath));
        Assert.Null(rig.Service.Snapshot().Pending);
    }

    // The event is saved before the request is written, and taken back when the request can't be.
    [Fact]
    public async Task Should_LogNothing_WhenTheRequestCantBeWritten()
    {
        await using var rig = await Rig.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Folder.RequestPath, "in-the-way"));

        Assert.Equal("Couldn't ask for the update. Try again in a while.", await rig.Service.RequestAsync(John, Cancel));

        Assert.Empty(await rig.EventsAsync());
        Assert.Null(rig.Service.Snapshot().Pending);
    }

    [Theory]
    [InlineData(UpdateStates.Done, "", EventActions.UpdateDone)]
    [InlineData(UpdateStates.WentBack, "reason=health", EventActions.UpdateWentBack)]
    [InlineData(UpdateStates.Failed, "reason=signature", EventActions.UpdateFailed)]
    [InlineData(UpdateStates.Current, "", EventActions.UpdateCurrent)]
    public async Task Should_LogHowItEnded_Once(string state, string more, string action)
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteStatus($"state={state}\n{more}");

        rig.Service.Poll();
        await rig.Service.FollowStatusAsync(Cancel);
        await rig.Service.FollowStatusAsync(Cancel);
        // A restart of the app (the new version, or the one gone back to) doesn't log it again either.
        await using var again = await Rig.StartAsync(rig);
        again.Service.Poll();
        await again.Service.FollowStatusAsync(Cancel);

        var ended = Assert.Single(await rig.EventsAsync());
        Assert.Equal((EventActors.System, action), (ended.Actor, ended.Action));
        Assert.Contains("\"id\":\"20261112-095800\"", ended.Details, StringComparison.Ordinal);
        Assert.Contains("\"to\":\"2026.11.20\"", ended.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_TakeBackARequest_TheHelperHasntTaken()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Service.RequestAsync(John, Cancel);

        await rig.Service.CancelAsync(Cancel);

        Assert.False(File.Exists(rig.Folder.RequestPath));
        Assert.Null(rig.Service.Snapshot().Pending);
    }

    [Theory]
    [InlineData(UpdateSteps.Download, true)]
    [InlineData(UpdateSteps.Backup, true)]
    [InlineData(UpdateSteps.Waiting, true)]
    [InlineData(UpdateSteps.Restart, false)]
    [InlineData(UpdateSteps.Check, false)]
    public async Task Should_AskTheHelperToCancel_OnlyUntilTheRestart(string step, bool cancels)
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteStatus($"state=running\nstep={step}");
        rig.Service.Poll();

        await rig.Service.CancelAsync(Cancel);

        Assert.Equal(cancels ? "cancel\n" : null, File.Exists(rig.Folder.RequestPath) ? await File.ReadAllTextAsync(rig.Folder.RequestPath, Cancel) : null);
    }

    [Fact]
    public async Task Should_KeepTheReleasesItHad_WhenGitHubCantBeReached()
    {
        var source = new FlakySource([new Release("2026.11.20", null, ["New"])]);
        await using var rig = await Rig.StartAsync(source: source);
        await rig.Service.CheckAsync(Cancel);

        source.Fails = true;
        await rig.Service.CheckAsync(Cancel);

        var releases = rig.Service.Snapshot().Releases;
        Assert.True(releases.Failed);
        Assert.Equal("2026.11.20", Assert.Single(releases.Releases).Version);
    }

    // Read once a day: a restart (e.g. the update's) doesn't ask GitHub again, it reads what was saved.
    [Fact]
    public async Task Should_SaveTheReleases_ForTheNextStart()
    {
        var source = new FlakySource([new Release("2026.11.20", new DateOnly(2026, 11, 20), ["New"])]);
        // The rig read them once.
        await using var rig = await Rig.StartAsync(source: source);

        source.Fails = true;
        await using var again = await Rig.StartAsync(rig, source);
        again.Service.LoadSavedReleases();

        var releases = again.Service.Snapshot().Releases;
        Assert.False(releases.Failed);
        Assert.Equal(new Release("2026.11.20", new DateOnly(2026, 11, 20), ["New"]), Assert.Single(releases.Releases), new ReleaseComparer());
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Should_KnowTheHelperIsThere_OnlyOnceItSaysSo()
    {
        await using var rig = await Rig.StartAsync(helper: false);
        Assert.False(rig.Service.Snapshot().HelperInstalled);

        await File.WriteAllTextAsync(rig.Folder.HelperPath, "version=1\n", Cancel);

        Assert.True(rig.Service.Poll());
        Assert.True(rig.Service.Snapshot().HelperInstalled);
    }

    private sealed class ReleaseComparer : IEqualityComparer<Release>
    {
        public bool Equals(Release? x, Release? y) => x is not null && y is not null && x.Version == y.Version && x.Date == y.Date && x.Changes.SequenceEqual(y.Changes);

        public int GetHashCode(Release obj) => obj.Version.GetHashCode(StringComparison.Ordinal);
    }

    internal sealed class FlakySource : IReleaseSource
    {
        private readonly IReadOnlyList<Release> _releases;

        public FlakySource(IReadOnlyList<Release> releases)
        {
            _releases = releases;
        }

        public bool Fails { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<Release>> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Fails ? throw new HttpRequestException("GitHub can't be reached.") : Task.FromResult(_releases);
        }
    }

    // A service on a database of its own, with a folder for the helper, its own clock, and releases newer than it.
    private sealed class Rig : IAsyncDisposable
    {
        private readonly bool _ownsDatabase;

        private Rig(TestDatabase database, UpdateFolder folder, UpdateService service, ManualTimeProvider time, bool ownsDatabase)
        {
            Database = database;
            Folder = folder;
            Service = service;
            Time = time;
            _ownsDatabase = ownsDatabase;
        }

        public TestDatabase Database { get; }

        public UpdateFolder Folder { get; }

        public UpdateService Service { get; }

        public ManualTimeProvider Time { get; }

        public static async Task<Rig> StartAsync(
            bool helper = true,
            IReadOnlyList<Release>? releases = null,
            AppBuild? build = null,
            IReleaseSource? source = null)
        {
            var database = TestDatabase.Create();
            await database.Services.InitializeDatabaseAsync(Cancel);
            await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
            {
                db.Users.Add(new User { Email = John.Email, DisplayName = John.DisplayName });
                await db.SaveChangesAsync(Cancel);
            }

            var folder = new UpdateFolder(Options.Create(new UpdateOptions()), database.Services.GetRequiredService<DatabaseLocation>());
            Directory.CreateDirectory(folder.Path);
            if (helper)
            {
                await File.WriteAllTextAsync(folder.HelperPath, "version=1\n", Cancel);
            }

            var time = new ManualTimeProvider();
            source ??= new TestWebApp.TestReleases(releases ?? [new Release("2026.11.20", null, ["New"])]);
            var service = new UpdateService(folder, source, database.Factory, build ?? new AppBuild("2026.11.14", null), time, NullLogger<UpdateService>.Instance, TestTexts.For<Contactor.Web.Resources.UpdatesTexts>());
            var rig = new Rig(database, folder, service, time, ownsDatabase: true);
            await service.CheckAsync(Cancel);
            service.Poll();
            return rig;
        }

        // The same installation after a restart: its database and folder, a new service.
        public static Task<Rig> StartAsync(Rig before, IReleaseSource? source = null)
        {
            var service = new UpdateService(before.Folder, source ?? new TestWebApp.TestReleases([]), before.Database.Factory, new AppBuild("2026.11.14", null), before.Time, NullLogger<UpdateService>.Instance, TestTexts.For<Contactor.Web.Resources.UpdatesTexts>());
            return Task.FromResult(new Rig(before.Database, before.Folder, service, before.Time, ownsDatabase: false));
        }

        // The helper's status for update 20261112-095800, from 2026.11.14 to 2026.11.20, with these lines too.
        public void WriteStatus(string lines)
        {
            var now = Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Folder.StatusPath, $"helper=1\nid=20261112-095800\nfrom=2026.11.14\nto=2026.11.20\nstarted={now}\nchanged={now}\n{lines}\n");
            // A new write, as the helper's: the service looks at the file's time first.
            File.SetLastWriteTimeUtc(Folder.StatusPath, File.GetLastWriteTimeUtc(Folder.StatusPath).AddSeconds(Interlocked.Increment(ref _writes)));
        }

        private static int _writes;

        public async Task<List<EventLogEntry>> EventsAsync()
        {
            await using var db = await Database.Factory.CreateDbContextAsync(Cancel);
            return await db.EventLog.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Cancel);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            if (_ownsDatabase)
            {
                await Database.DisposeAsync();
            }
        }
    }
}

/// <summary><c>update-backup</c>, which the helper runs in the app's container just before the restart.</summary>
public sealed class UpdateBackupTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_CopyTheDatabase_IntoTheUpdatesFolder()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);

        var (exitCode, output, error) = Run(database);

        var copy = Path.Combine(Path.GetDirectoryName(database.FilePath)!, "updates", UpdateFolder.BackupFileName);
        Assert.Equal((0, copy + "\n", ""), (exitCode, output, error));
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        Assert.True((long)(await command.ExecuteScalarAsync(Cancel))! > 0);
        Assert.False(File.Exists(copy + ".partial"));
    }

    // The restart would cut a group's start or run-on off: the helper waits and asks again.
    [Theory]
    [InlineData(GroupPhases.Starting)]
    [InlineData(GroupPhases.Stopping)]
    public async Task Should_CopyNothing_WhileAGroupIsStartingOrRunningOn(string phase)
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = await db.Boards.FirstAsync(Cancel);
            var group = new RelayGroup { Name = "Fan heater", BoardId = board.Id };
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id, Phase = phase });
            await db.SaveChangesAsync(Cancel);
        }

        var (exitCode, output, error) = Run(database);

        Assert.Equal((UpdateBackup.GroupsBusyExitCode, ""), (exitCode, output));
        Assert.StartsWith("update-backup: a group is starting or running on", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(database.FilePath)!, "updates", UpdateFolder.BackupFileName)));
    }

    [Fact]
    public async Task Should_SayThereIsNoDatabase()
    {
        await using var database = TestDatabase.Create();

        var (exitCode, output, error) = Run(database);

        Assert.Equal((1, ""), (exitCode, output));
        Assert.StartsWith("update-backup: there is no database at ", error, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error) Run(TestDatabase database)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        var exitCode = UpdateBackup.Run([UpdateBackup.Command, $"--ConnectionStrings:Default={database.ConnectionString}"], output, error);
        return (exitCode, output.ToString(), error.ToString());
    }
}

/// <summary>Updates on the Events screen.</summary>
public sealed class UpdateEventTextTests
{
    [Fact]
    public void Should_SayWhoStartedTheUpdate()
    {
        var view = TestTexts.Events.For(Entry("john@example.com", EventActions.UpdateRequested, "{\"from\":\"2026.11.14\",\"to\":\"2026.11.20\"}"), null, "John");

        Assert.Equal(new EventView(true, "J", "John", "{0} started the update to 2026.11.20", null), view);
    }

    [Fact]
    public void Should_SayItWasUpdated_WithItsLog()
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.UpdateDone, "{\"id\":\"20261115-140200\",\"from\":\"2026.11.14\",\"to\":\"2026.11.20\"}"), null, null);

        Assert.Equal(new EventView(false, "A", "Contactor", "{0} updated to 2026.11.20", null, Log: "settings/update-log?id=20261115-140200"), view);
    }

    [Fact]
    public void Should_SayItWentBack_InRed()
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.UpdateWentBack, "{\"id\":\"20261115-140200\",\"from\":\"2026.11.14\",\"to\":\"2026.11.20\",\"reason\":\"health\"}"), null, null);

        Assert.Equal(("Contactor", "went back to 2026.11.14: the update to 2026.11.20 didn't work", true), (view.Who, view.What, view.IsAlert));
    }

    [Theory]
    [InlineData("download", "didn't update to 2026.11.20: it couldn't be downloaded. Nothing was changed", true)]
    [InlineData("signature", "didn't update to 2026.11.20: it didn't pass the check that it's ours. Nothing was changed", true)]
    [InlineData("cancelled", "didn't update to 2026.11.20: the update was cancelled", false)]
    [InlineData("switching", "didn't update to 2026.11.20: the relays were still switching. Nothing was changed", false)]
    public void Should_SayWhyItDidntUpdate(string reason, string what, bool alert)
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.UpdateFailed, $"{{\"id\":\"20261115-140200\",\"to\":\"2026.11.20\",\"reason\":\"{reason}\"}}"), null, null);

        Assert.Equal((what, alert), (view.What, view.IsAlert));
    }

    [Fact]
    public void Should_SayThereWasNothingNewer()
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.UpdateCurrent, "{\"id\":\"20261115-140200\",\"from\":\"2026.11.14\",\"to\":\"2026.11.14\"}"), null, null);

        Assert.Equal(("Contactor", "found nothing newer than 2026.11.14 to download", false), (view.Who, view.What, view.IsAlert));
    }

    [Fact]
    public void Should_LinkNoLog_ForAnIdThatIsntOne()
    {
        var view = TestTexts.Events.For(Entry(EventActors.System, EventActions.UpdateDone, "{\"id\":\"../x\",\"to\":\"2026.11.20\"}"), null, null);

        Assert.Null(view.Log);
    }

    private static EventLogEntry Entry(string actor, string action, string details)
    {
        return new EventLogEntry { Actor = actor, Action = action, Details = details, TimestampUtc = DateTime.UtcNow };
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
