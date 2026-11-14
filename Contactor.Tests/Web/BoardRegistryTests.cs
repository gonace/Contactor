using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Tests.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>Every board the app runs, one live board per board's row, and replacing a board's model while the app runs.</summary>
public sealed class BoardRegistryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_HaveNoRelaysAndRefuseCommands_BeforeItsModelIsChosen()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database, type: "");
        var registry = Registry(database);

        await registry.LoadAsync(Cancel);

        var board = Assert.Single(registry.All);
        Assert.Same(board, registry.Find(id));
        Assert.Null(registry.Find(id + 1));
        Assert.Null(board.Active);
        Assert.Same(LiveBoard.NoBoard, board.Capabilities);
        Assert.Same(BoardState.Initial, board.State);
        Assert.False(board.Endpoint.HasHost);
        Assert.True(board.Login.IsNone);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOffAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
        Assert.Throws<InvalidOperationException>(() => board.SetEndpoint(new BoardEndpoint("127.0.0.1", 9760)));
        Assert.Throws<InvalidOperationException>(() => board.SetLogin(BoardLogin.None));
        Assert.Equal("No relay board has been chosen yet.", board.CheckLogin(BoardLogin.None));
    }

    [Fact]
    public async Task Should_PassEverythingToTheBoardsModel()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var parts = new FakeBoardParts(relayCount: 4);

        var board = await Registry(database).ReplaceAsync(parts.Build(id, Vm204Model.Model), Cancel);
        await board.TurnOnAsync(ChannelMask.Of(2), Cancel);
        board.SetEndpoint(new BoardEndpoint("10.0.0.9", 80));

        Assert.Equal(Vm204Model.Model, board.Model);
        Assert.Same(parts.Board.Capabilities, board.Capabilities);
        Assert.True(board.State.IsOn(2));
        Assert.Equal(new BoardEndpoint("10.0.0.9", 80), parts.Connection.Endpoint);
        Assert.Equal(parts.Connection.Endpoint, board.Endpoint);
    }

    // The new model is unknown until it answers, as at a new address, so the screens and the recorder start afresh;
    // nothing more comes from the old one, to the board's listeners, the registry's or the one-board bridge's.
    [Fact]
    public async Task Should_ReportTheNewModelsState_AtTheReplacement_AndNothingMoreFromTheOldOne()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();
        var next = new FakeBoardParts(connected: false);
        var board = await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        var states = new List<BoardState>();
        var changes = new List<BoardStateChange>();
        var bridged = new List<BoardState>();
        var traffic = new List<BoardTraceEntry>();
        board.StateChanged += (_, state) => states.Add(state);
        registry.StateChanged += (_, change) => changes.Add(change);
        registry.Only.StateChanged += (_, state) => bridged.Add(state);
        board.Traced += (_, entry) => traffic.Add(entry);

        await registry.ReplaceAsync(next.Build(id, Vm204Model.Model), Cancel);
        old.Fake.SetRelay(1, on: true);
        old.Traffic.Trace("old");
        next.Fake.SetRelay(2, on: true);
        next.Traffic.Trace("new");

        Assert.Equal(2, states.Count);
        Assert.False(states[0].HasAnswered);
        Assert.True(states[1].IsOn(2));
        Assert.Equal(states.Select(s => new BoardStateChange(id, s)), changes);
        Assert.Equal(states, bridged);
        Assert.Equal("new", Assert.Single(traffic).Text);
        Assert.Equal("new", Assert.Single(board.Trace.Items).Text);
        Assert.False(old.Fake.HasListeners);
        Assert.False(old.Traffic.HasListeners);
        Assert.True(old.Connection.Disposed);
    }

    // A listener to the one-board bridge from before the board's row was made (the setup guide) sees the board's state,
    // unknown until it answers, once it gets its model.
    [Fact]
    public async Task Should_TellTheBridgesListeners_AboutABoardAddedSinceTheyListened()
    {
        await using var database = await DatabaseAsync();
        var registry = Registry(database);
        var bridged = new List<BoardState>();
        var added = 0;
        registry.Only.StateChanged += (_, state) => bridged.Add(state);
        registry.BoardsChanged += (_, _) => added++;
        Assert.Null(registry.Only.Board);
        var id = await AddRowAsync(database);
        var parts = new FakeBoardParts(connected: false);

        var board = await registry.ReplaceAsync(parts.Build(id), Cancel);
        parts.Fake.SetConnected(true);

        Assert.Same(board, registry.Only.Board);
        Assert.Equal(1, added);
        Assert.Equal(2, bridged.Count);
        Assert.False(bridged[0].HasAnswered);
        Assert.True(bridged[1].IsConnected);
    }

    [Fact]
    public async Task Should_RunTheModel_AndTheNextOneAfterAReplacement()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();
        var next = new FakeBoardParts();
        old.Extra.ConnectionRunning = () => old.Connection.Running;
        next.Extra.ConnectionRunning = () => next.Connection.Running;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);

        // Nothing to run yet: it waits for a board.
        await Task.Delay(100, Cancel);
        Assert.False(run.IsCompleted);
        await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        await WaitUntilAsync(() => old.Connection.Running && old.Extra.Started);

        await registry.ReplaceAsync(next.Build(id, Vm204Model.Model), Cancel);
        await WaitUntilAsync(() => next.Connection.Running && next.Extra.Started);

        // What only the model has started before its connection. The old one stopped, what only it had too, and was
        // disposed once its run ended.
        Assert.True(old.Extra.StartedBeforeConnection);
        Assert.True(next.Extra.StartedBeforeConnection);
        Assert.False(old.Connection.Running);
        Assert.True(old.Extra.Stopped);
        await WaitUntilAsync(() => old.Connection.Disposed);
        Assert.False(next.Connection.Disposed);

        await stop.CancelAsync();
        await run;
        Assert.True(next.Extra.Stopped);
        Assert.False(next.Connection.Disposed);
        await registry.DisposeAsync();
        Assert.True(next.Connection.Disposed);
        Assert.False(next.Fake.HasListeners);
        Assert.False(next.Traffic.HasListeners);
    }

    // One board's run that fails, or ends cancelled while nothing stops it (e.g. a timeout), would leave that board
    // without a connection: the others stop and RunAsync fails, so the host stops the app and the container restarts it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_StopTheOtherBoards_AndFail_WhenOneBoardsRunEnds(bool cancelled)
    {
        await using var database = await DatabaseAsync();
        var first = await AddRowAsync(database, sortOrder: 1);
        var second = await AddRowAsync(database, sortOrder: 2);
        var registry = Registry(database);
        var failing = new FakeBoardParts();
        var other = new FakeBoardParts();
        await registry.ReplaceAsync(failing.Build(first), Cancel);
        await registry.ReplaceAsync(other.Build(second), Cancel);
        var run = registry.RunAsync(Cancel);
        await WaitUntilAsync(() => failing.Connection.Running && other.Connection.Running);

        Exception failure = cancelled ? new TaskCanceledException("A request timed out.") : new InvalidOperationException("The board's client broke.");
        failing.Connection.Fail(failure);

        // A cancelled run keeps no exception of its own: the registry says which board's ended.
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => run);
        if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.Contains($"Relay board {first}'s connection ended", thrown.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Same(failure, thrown);
        }

        Assert.False(other.Connection.Running);
        Assert.True(other.Extra.Stopped);
        Assert.True(failing.Extra.Stopped);
    }

    // Stopping the app is quiet, also for a board whose connection ends cancelled by the stop.
    [Fact]
    public async Task Should_StopQuietly_WhenTheAppStops()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        await registry.ReplaceAsync(parts.Build(id), Cancel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);
        await WaitUntilAsync(() => parts.Connection.Running);

        await stop.CancelAsync();

        await run;
        Assert.False(parts.Connection.Running);
        Assert.True(parts.Extra.Stopped);
    }

    [Fact]
    public async Task Should_DisposeTheOldModelAtOnce_WhenItIsNotRunning()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var old = new FakeBoardParts();

        await registry.ReplaceAsync(old.Build(id, Vm201Model.Model), Cancel);
        await registry.ReplaceAsync(new FakeBoardParts().Build(id, Vm204Model.Model), Cancel);

        Assert.True(old.Connection.Disposed);
        Assert.False(old.Extra.Started);
    }

    // A board without an address tries nothing, and starts once one is set; at the start of its run it takes the
    // address saved on its row since it was built.
    [Fact]
    public async Task Should_WaitForAnAddress_AndTakeTheOneSavedOnItsRow_WhenItsRunStarts()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        parts.Connection.DefaultEndpoint = new BoardEndpoint("", 9760);
        var board = await registry.ReplaceAsync(parts.Build(id), Cancel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = registry.RunAsync(stop.Token);
        try
        {
            await Task.Delay(LiveBoard.AddressCheck + TimeSpan.FromMilliseconds(500), Cancel);
            Assert.Equal(0, parts.Connection.Runs);
            Assert.False(parts.Extra.Started);

            board.SetEndpoint(new BoardEndpoint("10.0.0.7", 9760));

            await WaitUntilAsync(() => parts.Connection.Running);
            Assert.True(parts.Extra.Started);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }

        // The next run (a new model) takes the address saved on the row by then.
        await SaveAddressAsync(database, id, "10.0.0.8", 9761);
        var next = new FakeBoardParts();
        await registry.ReplaceAsync(next.Build(id), Cancel);
        Assert.Equal(new BoardEndpoint("192.168.1.50", 9760), next.Connection.Endpoint);
        using var again = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var second = registry.RunAsync(again.Token);
        await WaitUntilAsync(() => next.Connection.Running);
        Assert.Equal(new BoardEndpoint("10.0.0.8", 9761), next.Connection.Endpoint);
        await again.CancelAsync();
        await second;
    }

    // Disposing lets go of everything: the model's events, its connection, and the one-board bridge's listener.
    [Fact]
    public async Task Should_LetGoOfEverything_WhenDisposed()
    {
        await using var database = await DatabaseAsync();
        var id = await AddRowAsync(database);
        var registry = Registry(database);
        var parts = new FakeBoardParts();
        var board = await registry.ReplaceAsync(parts.Build(id), Cancel);
        var bridged = 0;
        registry.Only.StateChanged += (_, _) => bridged++;

        await registry.DisposeAsync();
        parts.Fake.SetRelay(1, on: true);

        Assert.Empty(registry.All);
        Assert.Null(board.Active);
        Assert.False(parts.Fake.HasListeners);
        Assert.False(parts.Traffic.HasListeners);
        Assert.True(parts.Connection.Disposed);
        Assert.Equal(0, bridged);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => board.ReplaceAsync(new FakeBoardParts().Build(id)));
    }

    // Decided in Issue #4: the settings' address and login (install.sh's Vm201__Host and so on) are the first board's
    // only. Another board starts at the model's default port with no address and no login, until one is saved for it;
    // its other settings are the model's as for any board.
    [Fact]
    public async Task Should_GiveTheSettingsAddressAndLogin_ToTheFirstBoardOnly()
    {
        await using var database = await DatabaseAsync(new Dictionary<string, string?>
        {
            ["Vm201:Host"] = "10.0.0.1",
            ["Vm201:Port"] = "9999",
            ["Vm201:Username"] = "admin",
            ["Vm201:Password"] = "secret",
            ["Vm201:KeepAliveSeconds"] = "30",
        });
        var first = await AddRowAsync(database, sortOrder: 1);
        var saved = await AddRowAsync(database, sortOrder: 2);
        var none = await AddRowAsync(database, sortOrder: 3);
        await SaveAddressAsync(database, saved, "10.0.0.2", 9761);
        var registry = Registry(database);

        await registry.LoadAsync(Cancel);

        Assert.Equal([first, saved, none], registry.All.Select(b => b.BoardId));
        var (one, two, three) = (registry.Find(first)!, registry.Find(saved)!, registry.Find(none)!);
        Assert.Equal(new BoardEndpoint("10.0.0.1", 9999), one.Endpoint);
        Assert.Equal(("admin", "secret"), (one.Login.Username, one.Login.Password));
        Assert.Equal(new BoardEndpoint("10.0.0.2", 9761), two.Endpoint);
        Assert.Equal(new BoardEndpoint("", 9760), two.DefaultEndpoint);
        Assert.True(two.Login.IsNone);
        Assert.Equal(new BoardEndpoint("", 9760), three.Endpoint);
        Assert.True(three.DefaultLogin.IsNone);
        Assert.All(registry.All, b => Assert.Equal(30, b.Active!.Services.GetRequiredService<IOptions<Vm201Options>>().Value.KeepAliveSeconds));

        // What still runs one board can't tell them apart: it refuses rather than guess.
        Assert.Throws<InvalidOperationException>(() => registry.Only.Board);
    }

    // The setup guide's choice: a row made since start gets its live board, and choosing its model again changes nothing.
    [Fact]
    public async Task Should_AddTheRowsBoard_AndKeepItsModel_WhenChosenAgain()
    {
        await using var database = await DatabaseAsync(new Dictionary<string, string?> { ["Vm201:Host"] = "10.0.0.1" });
        var registry = Registry(database);
        await registry.LoadAsync(Cancel);
        var id = await AddRowAsync(database, name: "Garage");

        var board = await registry.ReplaceModelAsync(id, Vm201Model.Model, Cancel);
        var active = board.Active;
        Assert.Same(board, await registry.ReplaceModelAsync(id, Vm201Model.Model, Cancel));

        Assert.Same(active, board.Active);
        Assert.Equal(("Garage", Vm201Model.Model), (board.Name, board.Model));
        Assert.Equal(new BoardEndpoint("10.0.0.1", 9760), board.Endpoint);
        Assert.Equal(id, board.Active!.BoardId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceModelAsync(id + 1, Vm201Model.Model, Cancel));
    }

    private static BoardRegistry Registry(TestDatabase database)
    {
        return database.Services.GetRequiredService<BoardRegistry>();
    }

    private static async Task<TestDatabase> DatabaseAsync(IDictionary<string, string?>? settings = null)
    {
        var database = TestDatabase.Create((services, configuration) => services.AddBoards(configuration), settings);
        await database.Services.InitializeDatabaseAsync(Cancel);
        return database;
    }

    private static async Task<int> AddRowAsync(TestDatabase database, string type = "vm201", int sortOrder = 1, string name = "Board")
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = new Board { Name = name, Type = type, SortOrder = sortOrder };
        db.Boards.Add(board);
        await db.SaveChangesAsync(Cancel);
        return board.Id;
    }

    private static async Task SaveAddressAsync(TestDatabase database, int boardId, string host, int port)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var board = await db.Boards.SingleAsync(b => b.Id == boardId, Cancel);
        (board.Host, board.Port) = (host, port);
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
