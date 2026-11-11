using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Groups;
using Contactor.Web;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class RelaySettingsHandlerTests : IAsyncLifetime
{
    private const string Sam = "sam@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 13, 30, 0, DateTimeKind.Utc);

    private TestDatabase _database = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RelaySettingsHandler Handler => _database.Services.GetRequiredService<RelaySettingsHandler>();

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
        });
        await _database.Services.InitializeDatabaseAsync(Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Should_RenameAndLogTheOldAndNewName()
    {
        var error = await Handler.SaveAsync(new RelaySettings(4, "  Ventilation – Paint shop ", true, 120), Sam, Cancel);

        Assert.Null(error);
        Assert.Equal("Ventilation – Paint shop", (await Handler.GetAsync(4, Cancel))!.Name);
        var entry = Assert.Single(await EventsAsync());
        Assert.Equal((EventActions.Rename, Sam, 4), (entry.Action, entry.Actor, entry.RelayId!.Value));
        using var details = System.Text.Json.JsonDocument.Parse(entry.Details!);
        Assert.Equal("Relay 4", details.RootElement.GetProperty("from").GetString());
        Assert.Equal("Ventilation – Paint shop", details.RootElement.GetProperty("to").GetString());
        Assert.Equal(("renamed Relay 4 to", "Ventilation – Paint shop"), (EventText.For(entry, "Ventilation – Paint shop", "Sam").What, EventText.For(entry, null, "Sam").Target));
    }

    [Fact]
    public async Task Should_LogNothing_WhenNothingChanged()
    {
        Assert.Null(await Handler.SaveAsync(new RelaySettings(1, "Relay 1", true, 120), Sam, Cancel));

        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Should_LogTheChangeToActive()
    {
        await Handler.SaveAsync(new RelaySettings(6, "Relay 6", false, 120), Sam, Cancel);

        Assert.Equal([EventActions.Deactivate], (await EventsAsync()).Select(e => e.Action));
        Assert.False((await Handler.GetAsync(6, Cancel))!.IsActive);

        await Handler.SaveAsync(new RelaySettings(6, "Relay 6", true, 120), Sam, Cancel);
        Assert.Equal([EventActions.Deactivate, EventActions.Activate], (await EventsAsync()).Select(e => e.Action));
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("12345678901234567890123456789012345678901", "The name can be at most 40 characters.")]
    public async Task Should_RefuseNamesThatCannotBeShown(string name, string message)
    {
        Assert.Equal(message, await Handler.SaveAsync(new RelaySettings(1, name, true, 120), Sam, Cancel));

        Assert.Equal("Relay 1", (await Handler.GetAsync(1, Cancel))!.Name);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Should_RefuseALimitTheScreenDoesNotOffer()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Handler.SaveAsync(new RelaySettings(1, "Relay 1", true, 90), Sam, Cancel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Should_FindNoRelay_ForAnIdTheBoardDoesNotHave(int id)
    {
        Assert.Null(await Handler.GetAsync(id, Cancel));
        Assert.Equal("The relay doesn't exist.", await Handler.SaveAsync(new RelaySettings(id, "X", true, 120), Sam, Cancel));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(240, 240)]
    [InlineData(null, null)]
    public async Task Should_MoveTheAutoOffOfARelayThatIsOn(int? limit, int? minutesAfterOn)
    {
        var onSince = Now.AddMinutes(-30);
        await AddRuntimeAsync(new RelayRuntime { RelayId = 2, OnSinceUtc = onSince, AutoOffAtUtc = onSince.AddMinutes(120), TurnedOnBy = Sam });

        await Handler.SaveAsync(new RelaySettings(2, "Relay 2", true, limit), Sam, Cancel);

        var runtime = await RuntimeAsync(2);
        Assert.Equal(minutesAfterOn is { } m ? onSince.AddMinutes(m) : null, runtime.AutoOffAtUtc);
        Assert.Equal(limit, (await Handler.GetAsync(2, Cancel))!.MaxOnMinutes);
    }

    [Fact]
    public async Task Should_NotPlanAutoOff_ForARelayThatIsOff()
    {
        await AddRuntimeAsync(new RelayRuntime { RelayId = 2 });

        await Handler.SaveAsync(new RelaySettings(2, "Relay 2", true, 60), Sam, Cancel);

        Assert.Null((await RuntimeAsync(2)).AutoOffAtUtc);
    }

    [Fact]
    public void Should_WarnOnlyWhenTheNewLimitHasPassed()
    {
        var bySam = new RelayRuntime { RelayId = 1, OnSinceUtc = Now.AddMinutes(-90), TurnedOnBy = Sam };
        var bySchedule = new RelayRuntime { RelayId = 1, OnSinceUtc = Now.AddMinutes(-90), TurnedOnBy = EventActors.Schedule };

        Assert.True(RelaySettingsHandler.WouldTurnOffAtOnce(bySam, 60, Now));
        Assert.False(RelaySettingsHandler.WouldTurnOffAtOnce(bySam, 120, Now));
        Assert.False(RelaySettingsHandler.WouldTurnOffAtOnce(bySam, null, Now));
        Assert.False(RelaySettingsHandler.WouldTurnOffAtOnce(null, 60, Now));
        Assert.False(RelaySettingsHandler.WouldTurnOffAtOnce(new RelayRuntime { RelayId = 1 }, 60, Now));
        Assert.False(RelaySettingsHandler.WouldTurnOffAtOnce(bySchedule, 60, Now));
    }

    [Fact]
    public async Task Should_GiveNoAutoOff_ToARelayAScheduleTurnedOn()
    {
        var onSince = Now.AddMinutes(-90);
        await AddRuntimeAsync(new RelayRuntime { RelayId = 2, OnSinceUtc = onSince, TurnedOnBy = EventActors.Schedule });

        await Handler.SaveAsync(new RelaySettings(2, "Relay 2", true, 60), Sam, Cancel);

        Assert.Null((await RuntimeAsync(2)).AutoOffAtUtc);
        Assert.Equal(60, (await Handler.GetAsync(2, Cancel))!.MaxOnMinutes);
    }

    private async Task<List<EventLogEntry>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.EventLog.OrderBy(e => e.Id).ToListAsync(Cancel);
    }

    // Every relay has a runtime row from the start; this sets it.
    private async Task AddRuntimeAsync(RelayRuntime runtime)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        db.RelayRuntimes.Update(runtime);
        await db.SaveChangesAsync(Cancel);
    }

    private async Task<RelayRuntime> RuntimeAsync(int relayId)
    {
        await using var db = await _database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, Cancel);
    }
}
