using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Velleman;

/// <summary>The simulated VM204 as the firmware answers, spoken to with raw HTTP.</summary>
public sealed class Vm204SimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_AnswerAsTheFirmware()
    {
        await using var board = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        board.Start();

        Assert.Equal("400", Code(await GetAsync(board.Port, "/api/auth?user=admin&password=vm204")));
        Assert.Equal("400", Code(await GetAsync(board.Port, "/api/auth?user=admin")));
        var login = await GetAsync(board.Port, "/api/auth?user=admin&password=VM204");
        Assert.EndsWith($"{{\"ApiKey\":\"{board.Key}\"}}", login, StringComparison.Ordinal);
        Assert.Contains("Connection: close", login, StringComparison.Ordinal);

        // The key in any case, as strcasecmp; the route words as the firmware compares them.
        Assert.Equal("200", Code(await GetAsync(board.Port, $"/api/STATUS?key={board.Key.ToLowerInvariant()}")));
        Assert.Equal("400", Code(await GetAsync(board.Port, "/api/status?key=wrong")));
        Assert.Equal("400", Code(await GetAsync(board.Port, $"/api/relay/ON?key={board.Key}&relay=1")));
        Assert.Equal("404", Code(await GetAsync(board.Port, "/index.html")));
        Assert.Equal("400", Code(await GetAsync(board.Port, $"/api/status?key={board.Key}&x={new string('a', 300)}")));

        var on = await GetAsync(board.Port, $"/api/relay/on?key={board.Key}&relay=2,4,7");
        Assert.EndsWith("{\"relays\": [false,true,false,true],\"pulse\": [false,false,false,false],\"inputs\": [false,false,false,false],\"analog\": 0}", on, StringComparison.Ordinal);
        Assert.Equal(ChannelMask.Of([2, 4]), board.State.Relays);
    }

    // As startPulse and vTimerCallback: on at once, off when the time is up, the pulse flag meanwhile. A second pulse while
    // one runs changes nothing, and on or off doesn't stop the timer, so a relay turned on again still goes off at its end.
    [Fact]
    public async Task Should_PulseAsTheFirmware()
    {
        var time = new ManualTimeProvider();
        await using var board = new Vm204Simulator { TimeProvider = time };
        board.Start();

        var pulse = await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=1&time=10");
        Assert.EndsWith("{\"relays\": [true,false,false,false],\"pulse\": [true,false,false,false],\"inputs\": [false,false,false,false],\"analog\": 0}", pulse, StringComparison.Ordinal);
        Assert.Equal("400", Code(await GetAsync(board.Port, "/api/relay/pulse?key=wrong&relay=2&time=10")));
        await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=2&time=0");
        await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=3");
        Assert.Equal(ChannelMask.Of(1), board.State.Relays);

        time.Advance(TimeSpan.FromSeconds(4));
        await GetAsync(board.Port, $"/api/relay/off?key={board.Key}&relay=1");
        await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=1&time=60");
        Assert.Equal(ChannelMask.None, board.State.Relays);
        await GetAsync(board.Port, $"/api/relay/on?key={board.Key}&relay=1");
        Assert.Equal(ChannelMask.Of(1), board.Pulsing);

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(ChannelMask.None, board.State.Relays);
        Assert.Equal(ChannelMask.None, board.Pulsing);
    }

    // The pulse's timer is in the board's memory: a power cut ends it, and its end never comes after the power is back.
    [Fact]
    public async Task Should_ForgetAPulse_AtAPowerCut()
    {
        var time = new ManualTimeProvider();
        await using var board = new Vm204Simulator { TimeProvider = time };
        board.Start();
        await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=2&time=10");

        await board.CutPowerAsync();
        await board.RestorePowerAsync();
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ChannelMask.None, board.State.Relays);
        Assert.Equal(ChannelMask.None, board.Pulsing);

        // Nor does it turn off a relay switched on afterwards.
        await GetAsync(board.Port, $"/api/relay/pulse?key={board.Key}&relay=3&time=10");
        await board.CutPowerAsync();
        await board.RestorePowerAsync();
        await GetAsync(board.Port, $"/api/relay/on?key={board.Key}&relay=3");
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(ChannelMask.Of(3), board.State.Relays);
        Assert.Equal(ChannelMask.None, board.Pulsing);
    }

    // The login changed on the board: the old one is refused, the new one gives the key, and the key the app has works.
    [Fact]
    public async Task Should_TakeOnlyTheNewLogin_AfterItChanged()
    {
        await using var board = new Vm204Simulator("admin", "VM204", NullLogger.Instance);
        board.Start();

        board.ChangeLogin("admin", "Pear1");

        Assert.Equal("400", Code(await GetAsync(board.Port, "/api/auth?user=admin&password=VM204")));
        Assert.EndsWith($"{{\"ApiKey\":\"{board.Key}\"}}", await GetAsync(board.Port, "/api/auth?user=admin&password=Pear1"), StringComparison.Ordinal);
        Assert.Equal("200", Code(await GetAsync(board.Port, $"/api/status?key={board.Key}")));
        Assert.Throws<ArgumentException>(() => board.ChangeLogin("", "Pear1"));
    }

    [Fact]
    public async Task Should_TellWhoAskedRecently_AndWhatChangedAtTheBoard()
    {
        await using var board = new Vm204Simulator();
        var changes = 0;
        board.Changed += (_, _) => Interlocked.Increment(ref changes);
        board.Start();

        await board.SwitchRelayAsync(1, true);
        await board.SwitchRelayAsync(1, true);
        await board.SetInputAsync(4, true);
        await GetAsync(board.Port, "/api/auth?user=&password=");

        Assert.Equal(3, Volatile.Read(ref changes));
        Assert.Equal((ChannelMask.Of(1), ChannelMask.Of(4)), (board.State.Relays, board.State.Inputs));
        Assert.Equal(["127.0.0.1"], board.State.Clients);
        Assert.False(board.HasLogin);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SetInputAsync(5, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SwitchRelayAsync(5, true));
    }

    [Fact]
    public async Task Should_CloseAConnection_ThatSendsTooMuch()
    {
        await using var board = new Vm204Simulator();
        board.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", board.Port, Cancel);
        var stream = tcp.GetStream();

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /" + new string('a', 20_000)), Cancel);
        }
        catch (IOException)
        {
            // Closed while still writing: just as good.
        }

        var buffer = new byte[16];
        var read = await ReadOrZeroAsync(stream, buffer);
        Assert.Equal(0, read);
        Assert.Equal(0, board.RequestCount);
    }

    [Fact]
    public async Task Should_CloseAConnection_ThatSendsNoRequestWithinFiveSeconds()
    {
        var time = new ManualTimeProvider();
        await using var board = new Vm204Simulator { TimeProvider = time };
        board.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", board.Port, Cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/status"), Cancel);

        // The handler waits for the rest of the head, with its timeout on the clock.
        await Wait.UntilAsync(() => time.WaitsOnly(Vm204Simulator.RequestHeadTimeout));
        time.Advance(Vm204Simulator.RequestHeadTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, time.PendingTimers);
        time.Advance(TimeSpan.FromMilliseconds(1));

        var read = await ReadOrZeroAsync(stream, new byte[16]);
        Assert.Equal(0, read);
        Assert.Equal(0, board.RequestCount);
    }

    private static async Task<int> ReadOrZeroAsync(NetworkStream stream, byte[] buffer)
    {
        try
        {
            return await stream.ReadAsync(buffer, Cancel);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Code(string response)
    {
        return response.Split(' ')[1];
    }

    private static async Task<string> GetAsync(int port, string target)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: board\r\n\r\n"), Cancel);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(Cancel);
    }
}
