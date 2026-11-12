using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Vm204;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Vm204;

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
