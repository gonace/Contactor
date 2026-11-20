using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contactor.Tests.Web;

/// <summary>
/// A change the app asked for is on its way until the board's status shows it, not until the event log handles that
/// status: the recorder runs on its own loop and may be behind. Here it is held back (not started) until the end.
/// </summary>
public sealed class OnItsWayTests : IAsyncLifetime
{
    private const string Jane = "jane@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 2, 0, DateTimeKind.Utc);

    private readonly FakeRelayBoard _board = new(4);
    private readonly FixedTimeProvider _time = new(Now);
    private TestDatabase _database = null!;
    private BoardEventRecorder? _recorder;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelayControl Control => _database.Services.GetRequiredService<RelayControl>();

    private GroupSequencer Groups => _database.Services.GetRequiredService<GroupSequencer>();

    public async ValueTask InitializeAsync()
    {
        _board.Time = _time;
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddBoards(configuration);
            services.AddSingleton<TimeProvider>(_time);
            services.AddLocalClock(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddOwnRules(configuration);
            services.AddSchedules(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
        await _database.Services.UseFakeBoardAsync(_board);
    }

    public async ValueTask DisposeAsync()
    {
        if (_recorder is not null)
        {
            await _recorder.StopAsync(CancellationToken.None);
        }

        await _database.DisposeAsync();
    }

    // Issue #588: the start's ONs are still in the recorder's queue when the group stops. The stop finishes as soon as the
    // board shows both relays off, with one OFF each, not 5 s later or with an OFF sent again.
    [Fact]
    public async Task Should_FinishTheStop_AsSoonAsTheBoardShowsTheRelaysOff_WhileTheRecorderIsBehind()
    {
        var groupId = await AddGroupAsync((1, "Fan"), (2, "Heat"));
        await Groups.StartGroupAsync(groupId, Jane, Cancel);
        await Groups.StepAsync(Cancel);
        _time.Now += TimeSpan.FromSeconds(1);
        await Groups.StepAsync(Cancel);
        Assert.Equal(ChannelMask.Of([1, 2]), _board.State.Relays);

        await Groups.StopGroupAsync(groupId, Jane, Cancel);
        await Groups.StepAsync(Cancel);
        await Groups.StepAsync(Cancel);

        Assert.Equal(ChannelMask.None, _board.State.Relays);
        Assert.Equal(GroupPhases.Idle, (await GroupRunAsync(groupId)).Phase);
        Assert.Equal([(ChannelMask.Of(1), true), (ChannelMask.Of(2), true), (ChannelMask.Of(2), false), (ChannelMask.Of(1), false)], _board.Sent);
    }

    // The board showed Jane's ON before the recorder got to it, then the relay went off and on again at the board: the
    // recorder, once it catches up, still takes the ON as Jane's and logs the two changes after it as external.
    [Fact]
    public async Task Should_LogOnlyTheChangesOutsideTheApp_WhenTheRecorderCatchesUpLater()
    {
        await Control.TurnOnAsync(3, Jane, Cancel);
        _board.SetRelay(3, on: false);
        _board.SetRelay(3, on: true);

        _recorder = _database.Services.GetServices<IHostedService>().OfType<BoardEventRecorder>().Single();
        await _recorder.StartAsync(Cancel);

        List<(int RelayId, string Actor, string Action)> expected =
            [(3, Jane, EventActions.On), (3, EventActors.External, EventActions.Off), (3, EventActors.External, EventActions.On)];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await EventsAsync()).Count < expected.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Cancel);
        }

        Assert.Equal(expected, await EventsAsync());
    }

    private async Task<GroupRuntime> GroupRunAsync(int groupId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.GroupRuntimes.AsNoTracking().SingleAsync(r => r.GroupId == groupId, Cancel);
    }

    // The events, as (relay or 0, actor, action).
    private async Task<List<(int RelayId, string Actor, string Action)>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return [.. (await db.EventLog.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Cancel)).Select(e => (e.RelayId ?? 0, e.Actor, e.Action))];
    }

    private async Task<int> AddGroupAsync(params (int RelayId, string Role)[] members)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, IsActive = true, MaxOnMinutes = RelayGroup.DefaultMaxOnMinutes };
        foreach (var (relayId, role) in members)
        {
            group.AddStep(role, relayId, startDelaySec: 1, runOnSec: 0);
        }

        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }
}
