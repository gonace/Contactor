using Contactor.Boards;
using Contactor.Web.Features.Boards;

namespace Contactor.Tests.Web;

/// <summary>
/// The Debug screen's list of board traffic, whatever the board.
/// </summary>
public sealed class BoardTraceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTraffic _traffic = new();
    private readonly BoardTrace _trace;

    public BoardTraceTests()
    {
        _trace = new BoardTrace(_traffic);
    }

    public void Dispose()
    {
        _trace.Dispose();
    }

    [Fact]
    public void Should_KeepTheLatestHundredNewestFirst()
    {
        for (var i = 0; i < BoardTrace.Capacity + 5; i++)
        {
            _trace.Record(Step(Now.AddSeconds(i), "Trying again in 1 s"));
        }

        var items = _trace.Items;
        Assert.Equal(BoardTrace.Capacity, items.Count);
        Assert.Equal(Now.AddSeconds(BoardTrace.Capacity + 4), items[0].AtUtc);
        Assert.Equal(Now.AddSeconds(5), items[^1].AtUtc);
        Assert.True(items[0].Id > items[1].Id);
    }

    [Fact]
    public void Should_EmptyTheList_WhenCleared()
    {
        var cleared = 0;
        _trace.Cleared += (_, _) => cleared++;
        _trace.Record(Step(Now, "Ask for status"));

        _trace.Clear();

        Assert.Empty(_trace.Items);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Should_RecordWhatTheBoardReports_AndTellTheScreens()
    {
        List<BoardTraceItem> added = [];
        _trace.Added += (_, item) => added.Add(item);

        _traffic.Raise(new BoardTraceEntry(Now, BoardTraceDirection.Sent, "Turn on relay 3", "02 06 4F 04 A5 03", false, false));

        var item = Assert.Single(added);
        Assert.Equal(("Turn on relay 3", "02 06 4F 04 A5 03", BoardTraceDirection.Sent), (item.Text, item.Hex, item.Direction));
        Assert.Same(item, Assert.Single(_trace.Items));
    }

    [Fact]
    public void Should_StopListening_WhenDisposed()
    {
        _trace.Dispose();

        _traffic.Raise(Step(Now, "Ask for status"));

        Assert.Empty(_trace.Items);
    }

    private static BoardTraceEntry Step(DateTimeOffset at, string text)
    {
        return new BoardTraceEntry(at, BoardTraceDirection.None, text, null, false, false);
    }

    private sealed class FakeTraffic : IBoardTraffic
    {
        public event EventHandler<BoardTraceEntry>? Traced;

        public void Raise(BoardTraceEntry entry)
        {
            Traced?.Invoke(this, entry);
        }
    }
}
