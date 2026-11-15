using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Vm208;

namespace Contactor.Tests.Vm208;

/// <summary>The simulated VM208 as the firmware answers, spoken to with raw HTTP.</summary>
public sealed class Vm208SimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Busy for as many statuses as asked, a relay request switching all the same; then as usual. 0 or less: none.
    [Fact]
    public async Task Should_AnswerBusy_ForTheNextStatuses()
    {
        await using var board = new Vm208Simulator();
        board.Start();
        board.BusyNext = 2;

        Assert.EndsWith("{\"Channels\":[]}", await SendAsync(board.Port, "GET /status?interface=-1&socket=0"), StringComparison.Ordinal);
        Assert.EndsWith("{\"Channels\":[]}", await SendAsync(board.Port, "POST /relay", "index=2&state=1&interface=-1&socket=0"), StringComparison.Ordinal);
        Assert.Equal(0b0010, board.Relays);
        Assert.EndsWith(Vm208Json.WriteStatus(new Vm208Status(0b0010, false, 0)), await SendAsync(board.Port, "GET /status?interface=-1&socket=0"), StringComparison.Ordinal);

        board.BusyNext = -5;
        Assert.EndsWith(Vm208Json.WriteStatus(new Vm208Status(0b0010, false, 0)), await SendAsync(board.Port, "GET /status?interface=-1&socket=0"), StringComparison.Ordinal);
        Assert.Equal(-5, board.BusyNext);
    }

    [Fact]
    public async Task Should_AnswerAsTheFirmware()
    {
        await using var board = new Vm208Simulator();
        board.Start();

        var status = await SendAsync(board.Port, "GET /status?interface=-1&socket=0");
        Assert.Equal("200", Code(status));
        Assert.Contains("Connection: close", status, StringComparison.Ordinal);
        Assert.EndsWith(Vm208Json.WriteStatus(new Vm208Status(0, false, 0)), status, StringComparison.Ordinal);

        // Parameters by position, not by name, as the firmware reads them.
        Assert.Equal("200", Code(await SendAsync(board.Port, "GET /status?a=-1&b=7")));

        var on = await SendAsync(board.Port, "POST /relay", "index=2&state=1&interface=-1&socket=0");
        Assert.EndsWith(Vm208Json.WriteStatus(new Vm208Status(0b0010, false, 0)), on, StringComparison.Ordinal);
        await SendAsync(board.Port, "POST /relay", "index=4&state=1&interface=-1&socket=0");
        await SendAsync(board.Port, "POST /relay", "index=2&state=0&interface=-1&socket=0");
        Assert.Equal(ChannelMask.Of(4), board.State.Relays);

        // The board's own pages ask for their login; the routes the app doesn't use aren't there.
        Assert.Equal("401", Code(await SendAsync(board.Port, "GET /")));
        Assert.Equal("404", Code(await SendAsync(board.Port, "GET /relay")));
        Assert.Equal("404", Code(await SendAsync(board.Port, "POST /status", "interface=-1&socket=0")));
        Assert.Equal(0, board.UnansweredCount);
    }

    [Theory]
    [InlineData("GET /status", null)]
    [InlineData("GET /status?interface=-1", null)]
    [InlineData("GET /status?interface=-1&socket=0&x=1", null)]
    [InlineData("GET /status?interface=0&socket=0", null)]
    [InlineData("POST /relay", "index=1&state=1&interface=-1")]
    [InlineData("POST /relay", "index=5&state=1&interface=-1&socket=0")]
    [InlineData("POST /relay", "index=1&state=1&interface=2&socket=1")]
    public async Task Should_LeaveUnanswered_WhatTheFirmwareWouldNotAnswerOrWouldCrashOn(string request, string? form)
    {
        await using var board = new Vm208Simulator();
        board.Start();

        Assert.Equal("", await SendAsync(board.Port, request, form));

        Assert.Equal(1, board.UnansweredCount);
        Assert.Equal(ChannelMask.None, board.State.Relays);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "index=1&state=1&interface=-1&socket=0", true)]
    [InlineData("application/x-www-form-urlencoded; charset=UTF-8", "index=1&state=1&interface=-1&socket=0", true)]
    [InlineData("Application/X-WWW-Form-Urlencoded", "index=1&state=1&interface=-1&socket=0", false)]
    [InlineData("text/plain", "index=1&state=1&interface=-1&socket=0", true)]
    [InlineData("text/plain; charset=UTF-8", "index=1&state=1&interface=-1&socket=0", true)]
    [InlineData("text/plain ", "index=1&state=1&interface=-1&socket=0", true)]
    [InlineData("Text/Plain", "index=1&state=1&interface=-1&socket=0", false)]
    [InlineData("text/plain", "&index=1&state=1&interface=-1&socket=0", false)]
    [InlineData("text/plain", "{\"index\":1}", false)]
    [InlineData("application/json", "index=1&state=1&interface=-1&socket=0", false)]
    [InlineData(null, "index=1&state=1&interface=-1&socket=0", false)]
    public async Task Should_ReadTheForm_AsTheFirmwaresWebServerDoes(string? contentType, string body, bool read)
    {
        // ESPAsyncWebServer reads a body's parameters for a Content-Type that starts with
        // application/x-www-form-urlencoded, in that case, or for exactly text/plain with a body that starts like
        // "name=". Without them the firmware's /relay handler crashes, so the simulator leaves it unanswered.
        await using var board = new Vm208Simulator();
        board.Start();

        var answer = await SendAsync(board.Port, "POST /relay", body, contentType);

        if (read)
        {
            Assert.Equal("200", Code(answer));
            Assert.Equal(ChannelMask.Of(1), board.State.Relays);
        }
        else
        {
            Assert.Equal("", answer);
            Assert.Equal(1, board.UnansweredCount);
            Assert.Equal(ChannelMask.None, board.State.Relays);
        }
    }

    [Fact]
    public async Task Should_TellWhoAskedRecently_AndWhatChangedAtTheBoard()
    {
        await using var board = new Vm208Simulator();
        var changes = 0;
        board.Changed += (_, _) => Interlocked.Increment(ref changes);
        board.Start();

        await board.SwitchRelayAsync(1, true);
        await board.SwitchRelayAsync(1, true);
        await board.SetInputAsync(1, true);
        await SendAsync(board.Port, "GET /status?interface=-1&socket=0");

        Assert.Equal(3, Volatile.Read(ref changes));
        Assert.Equal((ChannelMask.Of(1), ChannelMask.Of(1)), (board.State.Relays, board.State.Inputs));
        Assert.Equal(["127.0.0.1"], board.State.Clients);
        Assert.False(board.HasLogin);
        Assert.Same(Vm208Model.Model, board.Model);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SetInputAsync(2, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SwitchRelayAsync(5, true));
    }

    [Fact]
    public async Task Should_TraceEachRequestAndAnswer_WithThePollsRoutine()
    {
        await using var board = new Vm208Simulator();
        List<BoardTraceEntry> traces = [];
        board.Traced += (_, entry) =>
        {
            lock (traces)
            {
                traces.Add(entry);
            }
        };
        board.Start();

        await SendAsync(board.Port, "GET /status?interface=-1&socket=0");
        await SendAsync(board.Port, "GET /status?interface=-1&socket=0");
        await SendAsync(board.Port, "POST /relay", "index=3&state=1&interface=-1&socket=0");

        lock (traces)
        {
            Assert.Equal(
                [
                    ("Ask for status", true), ("Status: all off · input off", false),
                    ("Ask for status", true), ("Status: all off · input off", true),
                    ("Turn on relay 3", false), ("Status: relay 3 on · input off", false),
                ],
                traces.Select(t => (t.Text, t.IsRoutine)));
            Assert.Equal("POST /relay index=3&state=1&interface=-1&socket=0", traces[4].Bytes);
        }
    }

    [Fact]
    public async Task Should_CloseAConnection_ThatSendsTooMuch_OrRefuseConnections()
    {
        await using var board = new Vm208Simulator();
        board.Start();

        Assert.Equal("", await SendAsync(board.Port, "POST /relay", new string('a', 5000)));
        board.RejectConnections = true;
        Assert.Equal("", await SendAsync(board.Port, "GET /status?interface=-1&socket=0"));

        Assert.Equal(0, board.RequestCount);
    }

    private static string Code(string response)
    {
        return response.Split(' ')[1];
    }

    // The whole answer, or "" when the board closed the connection without one.
    private static async Task<string> SendAsync(int port, string requestLine, string? form = null, string? contentType = "application/x-www-form-urlencoded")
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Cancel);
        var stream = tcp.GetStream();
        var type = contentType is null ? "" : $"Content-Type: {contentType}\r\n";
        var body = form is null ? "" : $"{type}Content-Length: {form.Length}\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine} HTTP/1.1\r\nHost: board\r\n{body}\r\n{form}"), Cancel);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(Cancel);
        }
        catch (IOException)
        {
            return "";
        }
    }
}
