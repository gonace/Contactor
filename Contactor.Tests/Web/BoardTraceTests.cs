using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Contactor.Vm201;
using Contactor.Web.Features.Vm201;

namespace Contactor.Tests.Web;

/// <summary>
/// The Debug screen's list of board traffic and its Swedish text.
/// </summary>
public sealed class BoardTraceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly BoardTrace _trace = new(new Vm201Client(
        Options.Create(new Vm201Options { Host = "127.0.0.1" }),
        TimeProvider.System,
        NullLogger<Vm201Client>.Instance));

    public void Dispose()
    {
        _trace.Dispose();
    }

    [Theory]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.On, 0x04, "Slå på relä 3")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.Off, 0xFF, "Stäng av alla")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.On, 0x15, "Slå på relä 1, 3 och 5")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.TimerDisable, 0xFF, "Stäng av kortets egna timers för alla")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.Toggle, 0x01, "Kommando T")]
    public void Should_DescribeACommand(Vm201TraceKind kind, byte command, byte mask, string expected)
    {
        var (text, hex, isError) = BoardTraceText.Describe(Packet(kind, command, mask));

        Assert.Equal(expected, text);
        Assert.False(isError);
        Assert.Equal(Hex(Vm201PacketCodec.Encode(command, mask)), hex);
    }

    [Fact]
    public void Should_DescribeTheVerifiedExampleWithItsBytes()
    {
        var (text, hex, _) = BoardTraceText.Describe(Packet(Vm201TraceKind.Sent, Vm201Command.On, 0x01));

        Assert.Equal(("Slå på relä 1", "02 06 4F 01 A8 03"), (text, hex));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, "Status: alla av · ingången av")]
    [InlineData(0x05, 0x00, 0x01, "Status: relä 1 och 3 på · ingången på")]
    [InlineData(0x80, 0x02, 0x00, "Status: relä 8 på · kortets timers på för relä 2 · ingången av")]
    public void Should_DescribeAStatus(byte outputs, byte timers, byte input, string expected)
    {
        var (text, _, isError) = BoardTraceText.Describe(Packet(Vm201TraceKind.Received, Vm201Command.Status, outputs, timers, input));

        Assert.Equal(expected, text);
        Assert.False(isError);
    }

    [Fact]
    public void Should_ShowAWrongLoginAsAnError()
    {
        var (text, _, isError) = BoardTraceText.Describe(Packet(Vm201TraceKind.Received, Vm201Command.AccessDenied));

        Assert.Equal("Fel användarnamn eller lösenord", text);
        Assert.True(isError);
    }

    [Fact]
    public void Should_NeverShowTheLogin()
    {
        var frame = Vm201PacketCodec.Encode(Vm201Command.Password, Vm201PacketCodec.EncodeCredential("secret"));
        var entry = new Vm201TraceEntry(Now, Vm201TraceKind.Sent, frame.AsMemory(0, 3), null);

        var (text, hex, _) = BoardTraceText.Describe(entry);

        Assert.Equal("Lösenord (dolt)", text);
        Assert.Equal("02 0E 57 …", hex);
    }

    [Theory]
    [InlineData(Vm201TraceKind.Connecting, "192.168.1.50:9760", "Ansluter till 192.168.1.50:9760", false)]
    [InlineData(Vm201TraceKind.ConnectionLost, "Connection refused", "Anslutningen bröts: Connection refused", true)]
    [InlineData(Vm201TraceKind.ConnectionLost, Vm201Client.StoppedByApp, "Appen stängde anslutningen", false)]
    [InlineData(Vm201TraceKind.Reconnecting, "4", "Försöker igen om 4 s", false)]
    [InlineData(Vm201TraceKind.DiscardedBytes, "3", "Hoppade över 3 trasiga byte från kortet", true)]
    public void Should_DescribeAConnectionStep(Vm201TraceKind kind, string detail, string expected, bool error)
    {
        var (text, hex, isError) = BoardTraceText.Describe(new Vm201TraceEntry(Now, kind, default, detail));

        Assert.Equal((expected, null, error), (text, hex, isError));
    }

    [Fact]
    public void Should_DescribeMalformedPacketsWithoutFailing()
    {
        var empty = new Vm201TraceEntry(Now, Vm201TraceKind.Received, new byte[] { 0x02 }, null);
        var shortStatus = new Vm201TraceEntry(Now, Vm201TraceKind.Received, Vm201PacketCodec.Encode(Vm201Command.Status, 0x01), null);

        Assert.Equal("Okänt svar 0x00", BoardTraceText.Describe(empty).Text);
        Assert.Equal("Okänt svar S", BoardTraceText.Describe(shortStatus).Text);
    }

    [Fact]
    public void Should_KeepTheLatestHundredNewestFirst()
    {
        for (var i = 0; i < BoardTrace.Capacity + 5; i++)
        {
            _trace.Record(new Vm201TraceEntry(Now.AddSeconds(i), Vm201TraceKind.Reconnecting, default, "1"));
        }

        var items = _trace.Items;
        Assert.Equal(BoardTrace.Capacity, items.Count);
        Assert.Equal(Now.AddSeconds(BoardTrace.Capacity + 4), items[0].AtUtc);
        Assert.Equal(Now.AddSeconds(5), items[^1].AtUtc);
        Assert.True(items[0].Id > items[1].Id);
    }

    [Fact]
    public void Should_MarkTheKeepAliveAsRoutine()
    {
        _trace.Record(Packet(Vm201TraceKind.Received, Vm201Command.Name, 1, 0x41));
        _trace.Record(Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x01, 0, 0));
        _trace.Record(Packet(Vm201TraceKind.Sent, Vm201Command.StatusRequest));
        _trace.Record(Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x01, 0, 0));
        _trace.Record(Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x03, 0, 0));
        _trace.Record(new Vm201TraceEntry(Now, Vm201TraceKind.Connecting, default, "192.168.1.50:9760"));
        _trace.Record(Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x03, 0, 0));

        Assert.Equal([true, false, true, true, false, false, false], _trace.Items.Reverse().Select(i => i.IsRoutine));
    }

    [Fact]
    public void Should_EmptyTheList_WhenCleared()
    {
        var cleared = 0;
        _trace.Cleared += (_, _) => cleared++;
        _trace.Record(Packet(Vm201TraceKind.Sent, Vm201Command.StatusRequest));

        _trace.Clear();

        Assert.Empty(_trace.Items);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Should_TellTheScreens_WhenAStepIsAdded()
    {
        List<BoardTraceItem> added = [];
        _trace.Added += (_, item) => added.Add(item);

        _trace.Record(Packet(Vm201TraceKind.Sent, Vm201Command.On, 0x04));

        Assert.Equal("Slå på relä 3", Assert.Single(added).Text);
    }

    private static Vm201TraceEntry Packet(Vm201TraceKind kind, byte command, params byte[] data)
    {
        return new Vm201TraceEntry(Now, kind, Vm201PacketCodec.Encode(command, data), null);
    }

    private static string Hex(byte[] bytes)
    {
        return string.Join(' ', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
