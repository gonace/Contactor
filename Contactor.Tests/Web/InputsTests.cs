using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>The board's inputs on the Relays screen and their own screen.</summary>
public sealed class InputsTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 13, 30, 0, DateTimeKind.Utc);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> John = new() { [TestWebApp.SignedInAs] = "john@example.com" };

    [Fact]
    public async Task Should_ListTheBoardsInputs_WithWhenTheyLastChanged()
    {
        await using var database = await DatabaseAsync(inputCount: 2, seededInputs: 4);
        await AddChangeAsync(database, inputId: 2, on: true, Now.AddMinutes(-28));
        await AddChangeAsync(database, inputId: 2, on: false, Now.AddMinutes(-10));

        var inputs = await Handler(database).ListAsync(Cancel);

        Assert.Equal([1, 2], inputs.Select(i => i.Id));
        Assert.Null(inputs[0].LastChange);
        Assert.Equal(new InputChange(Now.AddMinutes(-10), false), inputs[1].LastChange);
        Assert.Null(await Handler(database).GetAsync(3, Cancel));
    }

    // An event whose details can't be read says nothing about since when.
    [Fact]
    public async Task Should_SkipALastChange_ItCannotRead()
    {
        await using var database = await DatabaseAsync(inputCount: 1);
        await AddChangeAsync(database, inputId: 1, on: true, Now.AddMinutes(-20));
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = Now, InputId = 1, Actor = EventActors.External, Action = EventActions.InputChanged, Details = "not json" });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null((await Handler(database).GetAsync(1, Cancel))!.LastChange);
    }

    [Fact]
    public async Task Should_SaveNameAndActive()
    {
        await using var database = await DatabaseAsync(inputCount: 2);

        Assert.Null(await Handler(database).SaveAsync(new InputSettings(2, " Thermostat ", false), Cancel));

        var input = await Handler(database).GetAsync(2, Cancel);
        Assert.Equal(("Thermostat", false), (input!.Name, input.IsActive));
    }

    [Theory]
    [InlineData(1, " ", "Enter a name.")]
    [InlineData(1, "Door that is far too long for the name field", "The name can be at most 40 characters.")]
    [InlineData(3, "Door", "The input doesn't exist.")]
    public async Task Should_RefuseWhatItCannotSave(int id, string name, string message)
    {
        await using var database = await DatabaseAsync(inputCount: 2, seededInputs: 4);

        Assert.Equal(message, await Handler(database).SaveAsync(new InputSettings(id, name, true), Cancel));
    }

    [Theory]
    [InlineData(false, true, true, "Open since 14:20", false)]
    [InlineData(true, true, true, "Closed", false)]
    [InlineData(true, false, true, "Closed", false)]
    [InlineData(true, true, false, "Closed since unknown", true)]
    [InlineData(false, false, true, "Open since unknown", true)]
    [InlineData(false, true, false, "Open since 14:20", false)]
    public void Should_SaySinceWhen_OnlyWhenTheTimeIsKnown(bool closedNow, bool known, bool connected, string expected, bool unknown)
    {
        // The latest event says it opened at 13:20 UTC (14:20 in Stockholm). Closed now with contact: a change not logged
        // yet. Closed now without contact: the state shown may be stale, so "since unknown".
        var input = new InputView(1, 1, "Door", true, new InputChange(Now.AddMinutes(-10), false, known));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

        var status = TestTexts.Inputs.Status(input, closedNow, connected, TimeZoneInfo.ConvertTimeFromUtc(Now, zone), utc => TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

        Assert.Equal(new InputStatus(expected, unknown), status);
    }

    [Fact]
    public void Should_SaySinceUnknown_ForAnInputThatNeverChanged()
    {
        var input = new InputView(1, 1, "Door", true, null);

        Assert.Equal(new InputStatus("Open since unknown", true), TestTexts.Inputs.Status(input, false, true, Now, utc => utc));
        Assert.Equal(new InputStatus("Open since unknown", true), TestTexts.Inputs.Status(input, false, false, Now, utc => utc));
    }

    // A change seen live is known; one noticed after a gap isn't, nor is one with a long gap after it.
    [Fact]
    public async Task Should_KnowAChangesTime_UnlessNoticedOrFollowedByALongGap()
    {
        await using var database = await DatabaseAsync(inputCount: 3);
        await AddChangeAsync(database, inputId: 1, on: true, Now.AddMinutes(-30));
        await AddChangeAsync(database, inputId: 2, on: true, Now.AddMinutes(-30), noticed: true);
        await AddChangeAsync(database, inputId: 3, on: true, Now.AddMinutes(-90));
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.BoardRuntimes.SingleAsync(Cancel);
            (runtime.LastContactUtc, runtime.LongGapEndedUtc) = (Now, Now.AddMinutes(-60));
            await db.SaveChangesAsync(Cancel);
        }

        var inputs = await Handler(database).ListAsync(Cancel);

        Assert.Equal([true, false, false], inputs.Select(i => i.LastChange!.Known));
    }

    [Theory]
    [InlineData(false, true, "Show 1 inactive input")]
    [InlineData(true, false, "Show 1 inactive relay")]
    [InlineData(true, true, "Show 2 inactive")]
    public async Task Should_CountInactiveRelaysAndInputs_OnOneButton(bool relayInactive, bool inputInactive, string text)
    {
        await using var app = new TestWebApp("Development", John);
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            (await db.Relays.SingleAsync(r => r.Id == 8, Cancel)).IsActive = !relayInactive;
            (await db.Inputs.SingleAsync(i => i.Id == 1, Cancel)).IsActive = !inputInactive;
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.Equal(!inputInactive, html.Contains("id=\"inputs-heading\"", StringComparison.Ordinal));
    }

    private static InputsHandler Handler(TestDatabase database)
    {
        return database.Services.GetRequiredService<InputsHandler>();
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }

    private static async Task<TestDatabase> DatabaseAsync(int inputCount, int? seededInputs = null)
    {
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddOwnRules(configuration);
            services.AddInputs(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 4, inputCount: seededInputs ?? inputCount, Cancel);
        await database.Services.UseFakeBoardAsync(new FakeRelayBoard(relayCount: 4, inputCount: inputCount), seed: false);
        return database;
    }

    private static async Task AddChangeAsync(TestDatabase database, int inputId, bool on, DateTime timestampUtc, bool noticed = false)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = timestampUtc,
            InputId = inputId,
            Actor = EventActors.External,
            Action = EventActions.InputChanged,
            Details = noticed ? $$"""{"on":{{(on ? "true" : "false")}},"noticed":true}""" : on ? """{"on":true}""" : """{"on":false}""",
        });
        await db.SaveChangesAsync(Cancel);
    }

    [Fact]
    public void Should_NameWhatAnInputControls()
    {
        Assert.Equal("Group: Heater", TestTexts.Inputs.Label(new InputActionTarget(null, null, 2, "Heater")));
        Assert.Equal("Relay 4: Lighting", TestTexts.Inputs.Label(new InputActionTarget(7, 4, null, "Lighting")));
        Assert.Equal("Relay : Gone", TestTexts.Inputs.Label(new InputActionTarget(7, null, null, "Gone")));
    }
}
