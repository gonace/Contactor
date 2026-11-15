using Contactor.Boards;
using Contactor.Boards.DAEnetIP2v2;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

public sealed class DAEnetIP2v2BoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("denkovi-daenetip2v2-8", 8)]
    [InlineData("denkovi-daenetip2v2-16", 16)]
    public void Should_DescribeBothRelayKitVariants(string id, int relays)
    {
        var model = Assert.Single(DAEnetIP2v2Models.All, candidate => candidate.Id == id);
        Assert.Equal(relays, model.Capabilities.RelayCount);
        Assert.Equal(0, model.Capabilities.InputCount);
        Assert.Equal(8, model.Capabilities.AnalogInputCount);
        Assert.False(model.Capabilities.ReportsChanges);
        Assert.Equal(80, model.DefaultPort);
        Assert.True(model.HasLogin);
        Assert.False(model.PasswordOnly);
    }

    [Theory]
    [InlineData("var IO = [0,0,0,0];", 8, 0UL)]
    [InlineData("var IO = [1,2,0,0];", 8, 2UL)]
    [InlineData("var IO = [1,2,0,0];", 16, 258UL)]
    public void Should_ParseTheDocumentedDigitalPortBytes(string body, int count, ulong expected)
    {
        Assert.Equal(expected, DAEnetIP2v2Client.ParseIo(body, count).Relays);
    }

    [Theory]
    [InlineData("denkovi-daenetip2v2-8", 8, 1UL)]
    [InlineData("denkovi-daenetip2v2-16", 16, 257UL)]
    public async Task Should_ControlRelaysThroughTheHttpSimulator(string id, int relayCount, ulong mask)
    {
        var model = Assert.Single(DAEnetIP2v2Models.All, candidate => candidate.Id == id);
        Assert.Equal(relayCount, model.Capabilities.RelayCount);
        await using var simulator = new DAEnetIP2v2Simulator(model, "operator", "secret", NullLogger.Instance);
        simulator.Start();
        using var client = new DAEnetIP2v2Client(
            Options.Create(new DAEnetIP2v2Options { Host = "127.0.0.1", Port = simulator.Port, Username = "operator", Password = "secret" }),
            model,
            TimeProvider.System,
            NullLogger<DAEnetIP2v2Client>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            Assert.Equal(mask, await client.TurnOnAsync(mask, Cancel));
            Assert.Equal(mask, simulator.Relays);
            Assert.Equal(mask, client.State.Relays);
            Assert.Equal(mask, await client.TurnOffAsync(mask, Cancel));
            Assert.Equal(0UL, simulator.Relays);
        }
        finally
        {
            await stop.CancelAsync();
            await run.WaitAsync(Cancel);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The simulated controller did not connect.");
            await Task.Delay(20, Cancel);
        }
    }
}
