using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;

namespace Contactor.Tests.Web;

public sealed class LocalClockTests
{
    [Theory]
    [InlineData("2026-01-15T12:00:00Z", "2026-01-15T13:00:00")] // Winter: UTC+1.
    [InlineData("2026-07-15T12:00:00Z", "2026-07-15T14:00:00")] // Summer: UTC+2.
    [InlineData("2026-03-29T00:59:00Z", "2026-03-29T01:59:00")] // Just before the spring change.
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29T03:00:00")] // 02:00 local doesn't exist.
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T02:30:00")] // First 02:30 in autumn.
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25T02:30:00")] // Second 02:30.
    public void Should_ShowStockholmTime(string utc, string local)
    {
        var clock = Create(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), clock.Now);
    }

    [Fact]
    public void Should_RefuseToStart_WhenTimeZoneIsUnknown()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LocalClock.ConfigurationKey] = "Mars/Olympus" })
            .Build();

        Assert.ThrowsAny<TimeZoneNotFoundException>(() => new ServiceCollection().AddLocalClock(configuration));
    }

    private static LocalClock Create(DateTimeOffset now)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLocalClock(configuration);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        return services.BuildServiceProvider().GetRequiredService<LocalClock>();
    }
}
