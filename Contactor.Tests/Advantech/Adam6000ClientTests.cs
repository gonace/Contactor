using Contactor.Boards.Advantech;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Advantech;

public sealed class Adam6000ClientTests
{
    [Fact]
    public async Task Should_ReadTheSimulatorAndSwitchOnlySelectedRelays()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero));
        await using var simulator = new Adam6000Simulator(Adam6000Models.Adam6066, NullLogger.Instance, time);
        simulator.Start();
        var options = Options.Create(new Adam6000Options { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 60 });
        using var client = new Adam6000Client(options, Adam6000Models.Adam6066, time, NullLogger<Adam6000Client>.Instance);
        using var stop = new CancellationTokenSource();
        var run = client.RunAsync(stop.Token);

        try
        {
            await WaitForStateAsync(client, state => state.IsConnected && state.Reading is not null);
            await simulator.SetInputAsync(2, closed: true);
            client.Refresh();
            await WaitForStateAsync(client, state => state.Inputs == 0b10);

            Assert.Equal(0b100101UL, await client.SetAsync(0b100101, on: true, TestContext.Current.CancellationToken));
            Assert.Equal(0b100101UL, client.State.Relays);
            Assert.Equal(0b000100UL, await client.SetAsync(0b000100, on: false, TestContext.Current.CancellationToken));
            Assert.Equal(0b100001UL, client.State.Relays);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    private static async Task<Adam6000State> WaitForStateAsync(Adam6000Client client, Func<Adam6000State, bool> condition)
    {
        if (condition(client.State))
        {
            return client.State;
        }

        var changed = new TaskCompletionSource<Adam6000State>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Adam6000State> handler = (_, state) =>
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
