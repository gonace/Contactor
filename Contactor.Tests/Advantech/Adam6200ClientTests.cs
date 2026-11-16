using Contactor.Boards.Advantech;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Advantech;

public sealed class Adam6200ClientTests
{
    [Fact]
    public async Task Should_ReadTheSimulatorAndSwitchOnlySelectedRelays()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero));
        await using var simulator = new Adam6200Simulator(Adam6200Models.Adam6260, NullLogger.Instance, time);
        simulator.Start();
        var options = Options.Create(new Adam6200Options { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 60, UnitId = 7 });
        using var client = new Adam6200Client(options, Adam6200Models.Adam6260, time, NullLogger<Adam6200Client>.Instance);
        using var stop = new CancellationTokenSource();
        var run = client.RunAsync(stop.Token);

        try
        {
            await WaitForStateAsync(client, state => state.IsConnected && state.Reading is not null);
            Assert.Equal(0b100101UL, await client.SetAsync(0b100101, on: true, TestContext.Current.CancellationToken));
            Assert.Equal(0b100101UL, client.State.Relays);
            Assert.Equal(0b000100UL, await client.SetAsync(0b000100, on: false, TestContext.Current.CancellationToken));
            Assert.Equal(0b100001UL, client.State.Relays);
            Assert.Equal(0, client.Model.Capabilities.InputCount);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    private static async Task<Adam6200State> WaitForStateAsync(Adam6200Client client, Func<Adam6200State, bool> condition)
    {
        if (condition(client.State))
        {
            return client.State;
        }

        var changed = new TaskCompletionSource<Adam6200State>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Adam6200State> handler = (_, state) =>
        {
            if (condition(state))
            {
                changed.TrySetResult(state);
            }
        };
        client.StateChanged += handler;
        try
        {
            if (condition(client.State))
            {
                return client.State;
            }
            return await changed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            client.StateChanged -= handler;
        }
    }
}
