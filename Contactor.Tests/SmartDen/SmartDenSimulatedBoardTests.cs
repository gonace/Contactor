using Contactor.Boards;
using Contactor.Boards.SmartDen;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.SmartDen;

public sealed class SmartDenSimulatedBoardTests
{
    [Fact]
    public async Task Should_ConnectReadAndSwitchRelays_OverOnePersistentModbusTcpConnection()
    {
        await using var simulator = new SmartDenSimulator(TimeProvider.System);
        simulator.Start();

        using var client = new SmartDenClient(
            Options.Create(new SmartDenOptions { Host = "127.0.0.1", Port = simulator.Port }),
            SmartDenModels.Ip16RMt,
            TimeProvider.System,
            NullLogger<SmartDenClient>.Instance);
        using var stopping = new CancellationTokenSource();
        var run = client.RunAsync(stopping.Token);

        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            Assert.Equal(1UL, await client.TurnOnAsync(ChannelMask.Of(1).Bits));
            Assert.Equal(1UL, simulator.Relays);
            Assert.Equal(1UL, await client.TurnOnAsync(ChannelMask.Of(16).Bits));
            Assert.Equal((1UL << 15) | 1UL, simulator.Relays);
            Assert.Equal(1UL, await client.TurnOffAsync(ChannelMask.Of(1).Bits));
            Assert.Equal(1UL << 15, simulator.Relays);
            Assert.Equal(1, simulator.ConnectionCount);
        }
        finally
        {
            await stopping.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Should_PulseARelay_AndTurnItOffOnTheBoardsClock()
    {
        await using var simulator = new SmartDenSimulator(TimeProvider.System);
        simulator.Start();

        using var client = new SmartDenClient(
            Options.Create(new SmartDenOptions { Host = "127.0.0.1", Port = simulator.Port }),
            SmartDenModels.Ip16RMt,
            TimeProvider.System,
            NullLogger<SmartDenClient>.Instance);
        using var stopping = new CancellationTokenSource();
        var run = client.RunAsync(stopping.Token);

        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            Assert.Equal(1UL, await client.PulseAsync(ChannelMask.Of(1).Bits, TimeSpan.FromMilliseconds(100)));
            Assert.Equal(1UL, simulator.Relays);
            await WaitUntilAsync(() => simulator.Relays == 0);
        }
        finally
        {
            await stopping.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Should_ReconnectAfterTheSocketIsDropped_AndReadTheBoardStateAgain()
    {
        await using var simulator = new SmartDenSimulator(TimeProvider.System);
        simulator.Start();

        using var client = new SmartDenClient(
            Options.Create(new SmartDenOptions { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 1 }),
            SmartDenModels.Ip16RMt,
            TimeProvider.System,
            NullLogger<SmartDenClient>.Instance);
        using var stopping = new CancellationTokenSource();
        var run = client.RunAsync(stopping.Token);

        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            await client.TurnOnAsync(ChannelMask.Of(4).Bits);
            simulator.DropConnections();
            await WaitUntilAsync(() => !client.State.IsConnected);
            await WaitUntilAsync(() => client.State.IsConnected);
            Assert.True(simulator.ConnectionCount == 1);
            Assert.Equal(ChannelMask.Of(4), client.State.Relays);
        }
        finally
        {
            await stopping.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Should_RefuseACommandWithoutChangingTheRelay()
    {
        await using var simulator = new SmartDenSimulator(TimeProvider.System);
        simulator.Start();
        simulator.FailRelays = ChannelMask.Of(3).Bits;

        using var client = new SmartDenClient(
            Options.Create(new SmartDenOptions { Host = "127.0.0.1", Port = simulator.Port }),
            SmartDenModels.Ip16RMt,
            TimeProvider.System,
            NullLogger<SmartDenClient>.Instance);
        using var stopping = new CancellationTokenSource();
        var run = client.RunAsync(stopping.Token);

        try
        {
            await WaitUntilAsync(() => client.State.IsConnected);
            await Assert.ThrowsAsync<BoardRefusedException>(() => client.TurnOnAsync(ChannelMask.Of(3).Bits));
            Assert.Equal(0UL, simulator.Relays);
        }
        finally
        {
            await stopping.CancelAsync();
            await run;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
