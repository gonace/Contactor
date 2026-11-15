using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

public sealed class AutoOffServiceTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_TurnOffAndLog_WhenTheTimeHasPassed()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        var turnedOff = await AutoOff(rig).CheckAsync(Cancel);

        Assert.Equal([3], turnedOff);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(3));
        await rig.WaitForEventsAsync(events => events.Count == 2);
        var off = (await rig.EventsAsync())[1];
        Assert.Equal((EventActors.AutoOff, EventActions.Off, 3, """{"afterMinutes":120}"""), (off.Actor, off.Action, off.RelayId!.Value, off.Details));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    [Fact]
    public async Task Should_NotLogTheBoardsAnswerAsExternal()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));
        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        await AutoOff(rig).CheckAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(3));
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
    }

    // A relay on with nobody's run (here after an OFF the board was sent but didn't carry out, which clears the run
    // while the relay stays on) gets its own auto-off at the next check. One in a group is left to its group, a
    // person's run is kept, and "Never" gives none.
    [Fact]
    public async Task Should_GiveARelayOnWithNobodysRun_ItsOwnAutoOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan" });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
            db.RelayGroups.Add(group);
            (await db.Relays.SingleAsync(r => r.Id == 5, Cancel)).MaxOnMinutes = null;
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Control.TurnOnAsync(4, Jane, Cancel);
        await rig.Simulator.SetOutputsAsync(0b0001_1101);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.Outputs == 0b0001_1101);
        await rig.WaitForEventsAsync(events => events.Count(e => e.Actor == EventActors.External) == 3);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            // The runs as an OFF that wasn't carried out leaves them.
            foreach (var runtime in await db.RelayRuntimes.Where(r => r.RelayId == 3 || r.RelayId == 5).ToListAsync(Cancel))
            {
                (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy) = (null, null, null);
            }

            await db.SaveChangesAsync(Cancel);
        }

        rig.Time.Now += TimeSpan.FromMinutes(1);
        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        var now = rig.Time.Now.UtcDateTime;
        var relay3 = await RuntimeAsync(rig, 3);
        Assert.Equal(((DateTime?)now, EventActors.External, (DateTime?)now.AddMinutes(Relay.DefaultMaxOnMinutes)), (relay3.OnSinceUtc, relay3.TurnedOnBy, relay3.AutoOffAtUtc));
        var relay5 = await RuntimeAsync(rig, 5);
        Assert.Equal((EventActors.External, (DateTime?)null), (relay5.TurnedOnBy, relay5.AutoOffAtUtc));
        Assert.Equal(Jane, (await RuntimeAsync(rig, 4)).TurnedOnBy);
        Assert.Null((await RuntimeAsync(rig, 1)).TurnedOnBy);
    }

    private static async Task<RelayRuntime> RuntimeAsync(BoardTestRig rig, int relayId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    [Fact]
    public async Task Should_WaitUntilTheTime()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes) - TimeSpan.FromSeconds(1);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
        Assert.True(rig.Client.State.IsOn(3));
    }

    [Fact]
    public async Task Should_OnlyForgetThePlan_WhenTheRelayIsAlreadyOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.RelayRuntimes.Update(new RelayRuntime
            {
                RelayId = 2,
                OnSinceUtc = BoardTestRig.Start.AddHours(-3),
                AutoOffAtUtc = BoardTestRig.Start.AddHours(-1),
                TurnedOnBy = Jane,
            });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        Assert.DoesNotContain(rig.Simulator.ReceivedCommands, p => p.Command == Vm201Command.Off);
        Assert.Empty(await rig.EventsAsync());
        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel)).AutoOffAtUtc);
    }

    // A relay the board shows off keeps no run, also one with no auto-off due (or none at all): e.g. it went off while
    // the app was down, or the recorder's "off outside the app" gave way to another writer. Nothing is sent or logged.
    [Theory]
    [InlineData(null)]
    [InlineData(60)]
    public async Task Should_EndTheRunOfARelayThatIsOff(int? autoOffInMinutes)
    {
        await using var rig = await BoardTestRig.StartAsync();
        await SaveRunAsync(rig, relayId: 2, autoOffInMinutes);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        var run = await RuntimeAsync(rig, 2);
        Assert.Equal(((DateTime?)null, (string?)null, (DateTime?)null), (run.OnSinceUtc, run.TurnedOnBy, run.AutoOffAtUtc));
        Assert.DoesNotContain(rig.Simulator.ReceivedCommands, p => p.Command == Vm201Command.Off);
        Assert.Empty(await rig.EventsAsync());
    }

    // A pulse's run ends at the next check after the board turned the relay off by itself, as it should: that is only
    // a debug line, where another run left on a relay that is off is worth an information line.
    [Fact]
    public async Task Should_EndAPulsesRun_WithOnlyADebugLine()
    {
        var logs = new LevelCollector();
        await using var rig = await BoardTestRig.StartAsync(logs: logs);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var pulse = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel);
            pulse.OnSinceUtc = BoardTestRig.Start.AddSeconds(-3);
            pulse.AutoOffAtUtc = pulse.OnSinceUtc + TimeSpan.FromSeconds(2) + RelayControl.PulseBackstop;
            pulse.TurnedOnBy = Jane;
            await db.SaveChangesAsync(Cancel);
        }

        await SaveRunAsync(rig, relayId: 4, autoOffInMinutes: 60);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        Assert.Null((await RuntimeAsync(rig, 2)).OnSinceUtc);
        Assert.Null((await RuntimeAsync(rig, 4)).OnSinceUtc);
        Assert.Contains((LogLevel.Debug, "Relay 2's pulse is over: its run ends"), logs.Lines);
        Assert.Contains((LogLevel.Information, "Relay 4 is off but has a run: ending it"), logs.Lines);
        Assert.DoesNotContain(logs.Lines, l => l.Message.StartsWith("Relay 2 ", StringComparison.Ordinal));
        Assert.Empty(await rig.EventsAsync());
    }

    // A person's or a schedule's run is saved before the board shows the relay on: while its ON is on its way, it stays.
    [Fact]
    public async Task Should_KeepTheRun_WhileItsOnIsOnItsWay()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await SaveRunAsync(rig, relayId: 2, autoOffInMinutes: null);
        rig.Database.Services.Live().Expected.Expect(ChannelMask.Of(2), on: true);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        Assert.Equal(Jane, (await RuntimeAsync(rig, 2)).TurnedOnBy);
    }

    // The check's state is from its start: a person's ON the board confirmed meanwhile (so it is no longer pending)
    // keeps its run.
    [Fact]
    public async Task Should_KeepARun_WhoseOnTheBoardConfirmedDuringTheCheck()
    {
        var fake = new FakeRelayBoard(relayCount: 4);
        var board = new StaleOnceBoard(fake, BoardState.Initial with { IsConnected = true, LastContactUtc = DateTimeOffset.UnixEpoch });
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        await fake.TurnOnAsync(ChannelMask.Of(2), Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await RelayRuns.StartAsync(db, 2, Jane, BoardTestRig.Start, BoardTestRig.Start.AddHours(2), Cancel);
            await RelayRuns.SaveAsync(db, Cancel);
        }

        await database.Services.GetRequiredService<AutoOffService>().CheckAsync(Cancel);

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(Jane, (await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel)).TurnedOnBy);
    }

    // The board is lost while auto-off sends its OFF: the rest of the check waits, so the run of a relay that looked off
    // is left as it is until there is contact again.
    [Fact]
    public async Task Should_LeaveTheRunsOfOffRelays_WhenTheBoardIsLostDuringTheCheck()
    {
        var fake = new FakeRelayBoard(relayCount: 4);
        var board = new StaleOnceBoard(fake, BoardState.Initial with { IsConnected = true, Relays = ChannelMask.Of(1), LastContactUtc = DateTimeOffset.UnixEpoch });
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BoardTestRig.Start)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, Cancel);
        await database.Services.UseFakeBoardAsync(board);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            await RelayRuns.StartAsync(db, 1, Jane, BoardTestRig.Start.AddHours(-3), BoardTestRig.Start.AddHours(-1), Cancel);
            await RelayRuns.StartAsync(db, 2, Jane, BoardTestRig.Start.AddHours(-3), null, Cancel);
            await RelayRuns.SaveAsync(db, Cancel);
        }

        fake.SetConnected(false);

        Assert.Empty(await database.Services.GetRequiredService<AutoOffService>().CheckAsync(Cancel));

        await using var check = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Equal(Jane, (await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel)).TurnedOnBy);
    }

    /// <summary>A board whose first state read is an older one, as when the check read it before the board changed.</summary>
    private sealed class StaleOnceBoard : IRelayBoard
    {
        private readonly IRelayBoard _inner;
        private BoardState? _stale;

        public StaleOnceBoard(IRelayBoard inner, BoardState stale)
        {
            _inner = inner;
            _stale = stale;
        }

        public event EventHandler<BoardState>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public BoardCapabilities Capabilities => _inner.Capabilities;

        public BoardState State => Interlocked.Exchange(ref _stale, null) ?? _inner.State;

        public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
        {
            return _inner.TurnOnAsync(relays, cancellationToken);
        }

        public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
        {
            return _inner.TurnOffAsync(relays, cancellationToken);
        }

        public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
        {
            return _inner.PulseAsync(relays, length, cancellationToken);
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            return _inner.RefreshAsync(cancellationToken);
        }
    }

    private static async Task SaveRunAsync(BoardTestRig rig, int relayId, int? autoOffInMinutes)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var run = await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, Cancel);
        run.OnSinceUtc = BoardTestRig.Start.AddHours(-3);
        run.AutoOffAtUtc = autoOffInMinutes is { } minutes ? BoardTestRig.Start.AddMinutes(minutes) : null;
        run.TurnedOnBy = Jane;
        await db.SaveChangesAsync(Cancel);
    }

    [Fact]
    public async Task Should_WaitForTheBoard_WhenDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromHours(3);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.NotNull((await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel)).AutoOffAtUtc);
        }

        // Back after a while: the passed time is handled at the next check.
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        Assert.Equal([3], await AutoOff(rig).CheckAsync(Cancel));
    }

    [Fact]
    public async Task Should_LeaveRelaysWithoutALimitOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            (await db.Relays.SingleAsync(r => r.Id == 4, Cancel)).MaxOnMinutes = null;
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Control.TurnOnAsync(4, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(4));
        rig.Time.Now += TimeSpan.FromDays(2);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
    }

    private static AutoOffService AutoOff(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<AutoOffService>();
    }

    private sealed class LevelCollector : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Lines => _lines;

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue((logLevel, formatter(state, exception)));
        }

        public void Dispose()
        {
        }
    }
}
