using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

public sealed class Vm201EndpointTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(3);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("192.168.1.50", 9760)]
    [InlineData(" 192.168.1.51 ", 9760)]
    [InlineData("vm201.lan", 9760)]
    [InlineData("fe80::1", 1)]
    public void Should_AcceptAddresses(string host, int port)
    {
        var endpoint = Vm201Endpoint.TryCreate(host, port, out var error);

        Assert.NotNull(endpoint);
        Assert.Null(error);
        Assert.Equal(host.Trim(), endpoint.Host);
    }

    [Theory]
    [InlineData("", 9760, "Skriv reläkortets adress, t.ex. 192.168.1.50.")]
    [InlineData("   ", 9760, "Skriv reläkortets adress, t.ex. 192.168.1.50.")]
    [InlineData(null, 9760, "Skriv reläkortets adress, t.ex. 192.168.1.50.")]
    [InlineData("192.168.1.50/24", 9760, "192.168.1.50/24 är ingen adress. Skriv t.ex. 192.168.1.50.")]
    [InlineData("http://192.168.1.50", 9760, "http://192.168.1.50 är ingen adress. Skriv t.ex. 192.168.1.50.")]
    [InlineData("192.168.1.50", 0, "Porten ska vara ett tal mellan 1 och 65535 (oftast 9760).")]
    [InlineData("192.168.1.50", 70000, "Porten ska vara ett tal mellan 1 och 65535 (oftast 9760).")]
    public void Should_RefuseWhatIsNoAddress(string? host, int port, string message)
    {
        Assert.Null(Vm201Endpoint.TryCreate(host, port, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void Should_RefuseAHostTooLongForDns()
    {
        Assert.Null(Vm201Endpoint.TryCreate(new string('a', 254), 9760, out _));
    }

    [Fact]
    public async Task Should_MoveToTheNewBoardAtOnce()
    {
        await using var first = new Vm201Simulator();
        first.Start();
        await using var second = new Vm201Simulator();
        second.Start();
        await second.SetOutputsAsync(0x08);
        var client = Client(first.Port);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => client.State.IsConnected, TimeSpan.FromSeconds(10));

        client.SetEndpoint(new Vm201Endpoint("127.0.0.1", second.Port));

        await WaitUntilAsync(() => second.ConnectionCount == 1 && client.State.IsConnected && client.State.Outputs == 0x08, Fast);
        Assert.Equal(second.Port, client.Endpoint.Port);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Fact]
    public async Task Should_StopWaitingForAWrongAddress_WhenGivenTheRightOne()
    {
        await using var board = new Vm201Simulator();
        board.Start();
        var client = Client(port: 1);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);

        // Let it fail a few times so the wait grows.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Cancel);
        client.SetEndpoint(new Vm201Endpoint("127.0.0.1", board.Port));

        await WaitUntilAsync(() => client.State.IsConnected, Fast);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Fact]
    public async Task Should_KeepTheConnection_WhenGivenTheSameAddress()
    {
        await using var board = new Vm201Simulator();
        board.Start();
        var client = Client(board.Port);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => client.State.IsConnected, TimeSpan.FromSeconds(10));

        client.SetEndpoint(new Vm201Endpoint("127.0.0.1", board.Port));
        await Task.Delay(300, Cancel);

        Assert.Equal(1, board.ConnectionCount);
        Assert.True(client.State.IsConnected);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    private static Vm201Client Client(int port)
    {
        return new Vm201Client(
            Options.Create(new Vm201Options { Host = "127.0.0.1", Port = port }),
            TimeProvider.System,
            NullLogger<Vm201Client>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
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
