using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web;

/// <summary>
/// The app's local time (the <c>TimeZone</c> setting, Europe/Stockholm): everything is stored in UTC and shown in
/// this zone. Built on the injected <see cref="TimeProvider"/>, never the machine's clock or zone.
/// </summary>
public sealed class LocalClock
{
    public const string ConfigurationKey = "TimeZone";
    public const string DefaultTimeZone = "Europe/Stockholm";

    private readonly TimeProvider _timeProvider;

    public LocalClock(TimeZoneInfo zone, TimeProvider timeProvider)
    {
        Zone = zone;
        _timeProvider = timeProvider;
    }

    public TimeZoneInfo Zone { get; }

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public DateTime Now => ToLocal(UtcNow);

    public DateTime ToLocal(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    }
}

public static class LocalClockServiceCollectionExtensions
{
    /// <summary>Registers <see cref="LocalClock"/>; an unknown time zone stops the app at start.</summary>
    public static IServiceCollection AddLocalClock(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration[LocalClock.ConfigurationKey] ?? LocalClock.DefaultTimeZone);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new LocalClock(zone, sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
