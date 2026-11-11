using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Contactor.Vm201;

namespace Contactor.Tests.Vm201;

public sealed class Vm201PacketTraceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_ShowTheCommandAndTheBytes()
    {
        Assert.Equal("O 02 06 4F 01 A8 03", Vm201PacketTrace.Describe(Vm201PacketCodec.Encode(Vm201Command.On, 0x01)));
    }

    [Theory]
    [InlineData(Vm201Command.Username, "U (14 bytes, hidden)")]
    [InlineData(Vm201Command.Password, "W (14 bytes, hidden)")]
    public void Should_NeverShowCredentials(byte command, string expected)
    {
        var packet = Vm201PacketCodec.Encode(command, Vm201PacketCodec.EncodeCredential("secret"));

        Assert.Equal(expected, Vm201PacketTrace.Describe(packet));
    }

    [Fact]
    public void Should_CopeWithATooShortFrame()
    {
        Assert.Equal("? 02 05", Vm201PacketTrace.Describe([0x02, 0x05]));
    }

    [Fact]
    public async Task Should_LogEveryPacketButTheCredentials_WhenTurnedOn()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        simulator.Start();
        var logger = new CapturingLogger<Vm201Client>();
        var client = new Vm201Client(
            Options.Create(new Vm201Options { Host = "127.0.0.1", Port = simulator.Port, Username = "admin", Password = "secret", LogPackets = true }),
            TimeProvider.System,
            logger);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);

        await WaitUntilAsync(() => client.State.IsConnected);
        await client.TurnOnAsync(0x01, Cancel);
        await WaitUntilAsync(() => client.State.IsOn(1));
        await cts.CancelAsync();
        await run.WaitAsync(WaitTimeout, Cancel);

        var lines = logger.Messages;
        Assert.Contains("VM201 ← A 02 05 41 B8 03", lines);
        Assert.Contains("VM201 → U (14 bytes, hidden)", lines);
        Assert.Contains("VM201 → W (14 bytes, hidden)", lines);
        Assert.Contains("VM201 → O 02 06 4F 01 A8 03", lines);
        Assert.Contains(lines, l => l.StartsWith("VM201 ← S ", StringComparison.Ordinal));
        Assert.Contains("VM201 → C 02 05 43 B6 03", lines);
        Assert.DoesNotContain(lines, l => l.Contains("73 65 63 72 65 74", StringComparison.Ordinal)); // "secret" in hex.
    }

    [Fact]
    public async Task Should_LogNoPackets_ByDefault()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var logger = new CapturingLogger<Vm201Client>();
        var client = new Vm201Client(
            Options.Create(new Vm201Options { Host = "127.0.0.1", Port = simulator.Port }),
            TimeProvider.System,
            logger);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);

        await WaitUntilAsync(() => client.State.IsConnected);
        await cts.CancelAsync();
        await run.WaitAsync(WaitTimeout, Cancel);

        Assert.DoesNotContain(logger.Messages, l => l.StartsWith("VM201 →", StringComparison.Ordinal) || l.StartsWith("VM201 ←", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
