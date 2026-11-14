using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

public sealed class BoardAddressHandlerTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_UseTheSettings_UntilAnAddressIsSaved()
    {
        await using var rig = await BoardTestRig.StartAsync();

        var endpoint = await Handler(rig).GetAsync(rig.Live.BoardId, Cancel);

        Assert.Equal(new BoardEndpoint("127.0.0.1", rig.Simulator.Port), endpoint);
    }

    [Fact]
    public async Task Should_SaveLogAndMoveTheClient()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using var other = new Vm201Simulator();
        other.Start();
        var from = $"127.0.0.1:{rig.Simulator.Port}";

        Assert.Null(await Handler(rig).SaveAsync(rig.Live.BoardId, " 127.0.0.1 ", other.Port, John, Cancel));

        await BoardTestRig.WaitUntilAsync(() => other.ConnectionCount == 1 && rig.Client.State.IsConnected);
        Assert.Equal(new BoardEndpoint("127.0.0.1", other.Port), await Handler(rig).GetAsync(rig.Live.BoardId, Cancel));
        var entry = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.BoardAddressChanged);
        Assert.Equal(John, entry.Actor);
        Assert.Equal($"changed the relay board's address from {from} to 127.0.0.1:{other.Port}", EventText.For(entry, null, "John").What);
    }

    // The app can't tell a new board from the same board at a new IP, so a relay's run is kept: for the same board
    // clearing it would leave a fan heater without its auto-off.
    [Fact]
    public async Task Should_KeepTheRelaysRuns_AtANewAddress()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using var other = new Vm201Simulator();
        other.Start();
        var autoOff = BoardTestRig.Start.AddHours(2);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            (relay.OnSinceUtc, relay.AutoOffAtUtc, relay.TurnedOnBy) = (BoardTestRig.Start, autoOff, John);
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null(await Handler(rig).SaveAsync(rig.Live.BoardId, "127.0.0.1", other.Port, John, Cancel));

        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var after = await check.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(((DateTime?)autoOff, John), (after.AutoOffAtUtc, after.TurnedOnBy));
    }

    [Fact]
    public async Task Should_LogNothing_WhenTheAddressIsTheSame()
    {
        await using var rig = await BoardTestRig.StartAsync();

        Assert.Null(await Handler(rig).SaveAsync(rig.Live.BoardId, "127.0.0.1", rig.Simulator.Port, John, Cancel));
        Assert.Null(await Handler(rig).SaveAsync(rig.Live.BoardId, "127.0.0.1", rig.Simulator.Port, John, Cancel));

        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.BoardAddressChanged);
        Assert.Equal(1, rig.Simulator.ConnectionCount);
    }

    [Theory]
    [InlineData("", 9760)]
    [InlineData("192.168.1.50", 0)]
    [InlineData("not an address", 9760)]
    public async Task Should_SaveNothing_ForAWrongAddress(string host, int port)
    {
        await using var rig = await BoardTestRig.StartAsync();

        Assert.NotNull(await Handler(rig).SaveAsync(rig.Live.BoardId, host, port, John, Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await db.Boards.SingleAsync(Cancel)).Host);
        Assert.Equal(rig.Simulator.Port, rig.Client.Endpoint.Port);
    }

    [Fact]
    public async Task Should_SaveNothing_ForABoardThatDoesNotExist()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var missing = rig.Live.BoardId + 1;

        Assert.Equal(LiveBoard.NotChosen, await Handler(rig).SaveAsync(missing, "127.0.0.1", 9760, John, Cancel));
        Assert.Equal(LiveBoard.NotChosen, await Handler(rig).SaveLoginAsync(missing, "admin", "pw", none: false, John, Cancel));
        Assert.Equal(LiveBoard.NotChosen, await Handler(rig).RenameAsync(missing, "Workshop", John, Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.AsNoTracking().SingleAsync(Cancel);
        Assert.Equal(((string?)null, (string?)null), (board.Host, board.Username));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action is EventActions.BoardAddressChanged or EventActions.BoardLoginChanged);
    }

    [Fact]
    public async Task Should_UseTheSavedAddress_AfterARestart()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var board = await db.Boards.SingleAsync(Cancel);
            (board.Host, board.Port) = ("192.168.1.51", 9761);
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Live.ApplySavedAsync(Cancel);

        Assert.Equal(new BoardEndpoint("192.168.1.51", 9761), rig.Client.Endpoint);
    }

    // The address and login live on the board's row: saved, read back, applied at the next start, and logged with the
    // board, without the password anywhere in the log.
    [Fact]
    public async Task Should_KeepTheAddressAndLoginOnTheBoard_AndNeverLogThePassword()
    {
        const string Password = "pw7f3a9c";
        var logs = new LogCollector();
        await using var rig = await BoardTestRig.StartAsync(logs: logs);
        await using var other = new Vm201Simulator();
        other.Start();

        Assert.Null(await Handler(rig).SaveAsync(rig.Live.BoardId, "127.0.0.1", other.Port, John, Cancel));
        Assert.Null(await Handler(rig).SaveLoginAsync(rig.Live.BoardId, "admin", Password, none: false, John, Cancel));
        rig.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", rig.Simulator.Port));
        rig.Client.SetLogin(Vm201Login.None);
        await rig.Live.ApplySavedAsync(Cancel);

        Assert.Equal(new BoardEndpoint("127.0.0.1", other.Port), rig.Client.Endpoint);
        Assert.Equal(("admin", Password), (rig.Client.Login.Username, rig.Client.Login.Password));
        Assert.Equal(new BoardLoginView("admin", true), await Handler(rig).GetLoginAsync(rig.Live.BoardId, Cancel));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.AsNoTracking().SingleAsync(Cancel);
        Assert.Equal(("127.0.0.1", (int?)other.Port, "admin", Password), (board.Host, board.Port, board.Username, board.Password));
        var events = await rig.EventsAsync();
        Assert.Equal(2, events.Count(e => e.Action is EventActions.BoardAddressChanged or EventActions.BoardLoginChanged));
        Assert.All(events, e => Assert.Equal(board.Id, e.BoardId));
        Assert.All(events, e => Assert.DoesNotContain(Password, e.Details ?? "", StringComparison.Ordinal));
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(Password, StringComparison.Ordinal));
    }

    private static BoardAddressHandler Handler(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<BoardAddressHandler>();
    }

    private sealed class LogCollector : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

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
            _messages.Enqueue(formatter(state, exception) + " " + exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var (_, value) in fields)
                {
                    _messages.Enqueue(value?.ToString() ?? "");
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
