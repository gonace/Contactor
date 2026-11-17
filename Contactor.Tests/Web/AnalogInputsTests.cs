using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>The boards' analog inputs in the database: seeded per board, saved with their unit and scale, and checked.</summary>
public sealed class AnalogInputsTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);

    // Four analog inputs, 10 bits for 0–5 V.
    private static readonly AnalogAbility FourInputs = new(4, 1023, 5m);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_SeedAnAnalogInput_PerChannel_OnEachBoard_AndKeepThemAsTheyAre()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.SeedBoardAsync(new BoardCapabilities(4, 0, true, FourInputs), Cancel);
        var second = await AddBoardRowAsync(database, "Barn");
        await database.Services.SeedBoardAsync(second, new BoardCapabilities(2, 1, true, new AnalogAbility(2, 1023, 3.3m)), Cancel);
        await RenameAsync(database, boardId: 1, channel: 2, "Paint shop");

        // Seeded again, with fewer: nothing is renamed or removed.
        await database.Services.SeedBoardAsync(1, new BoardCapabilities(4, 0, true, new AnalogAbility(1, 1023, 5m)), Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var rows = await db.AnalogInputs.AsNoTracking().OrderBy(i => i.BoardId).ThenBy(i => i.Channel).ToListAsync(Cancel);
        Assert.Equal(
            [(1, 1, "Analog 1"), (1, 2, "Paint shop"), (1, 3, "Analog 3"), (1, 4, "Analog 4"), (second, 1, "Analog 1"), (second, 2, "Analog 2")],
            rows.Select(i => (i.BoardId, i.Channel, i.Name)));
        Assert.All(rows, i => Assert.Equal((true, "", "", false), (i.IsActive, i.Unit, i.UnitText, i.HasScale)));
    }

    // The setup guide's choice of model adds its rows: the VM204's analog input.
    [Fact]
    public async Task Should_SeedTheModelsAnalogInputs_WhenItIsChosen()
    {
        await using var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddBoards(configuration);
        });
        await database.Services.InitializeDatabaseAsync(Cancel);

        var board = await database.Services.UseBoardAsync("vm204");

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.True(board.Model.Capabilities.AnalogInputCount > 0);
        Assert.Equal(
            Enumerable.Range(1, board.Model.Capabilities.AnalogInputCount),
            await db.AnalogInputs.Where(i => i.BoardId == board.BoardId).OrderBy(i => i.Channel).Select(i => i.Channel).ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_SeedNoAnalogInput_ForABoardWithout()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, inputCount: 1, Cancel);
        await database.Services.SeedBoardAsync(new BoardCapabilities(8, 1, true), Cancel);

        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.AnalogInputs.AnyAsync(Cancel));
        Assert.Equal(8, await db.Relays.CountAsync(Cancel));
    }

    [Fact]
    public async Task Should_KeepTheScale_AsSaved()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.SeedBoardAsync(new BoardCapabilities(1, 0, true, FourInputs), Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var input = await db.AnalogInputs.SingleAsync(i => i.Channel == 1, Cancel);
            (input.Unit, input.UnitText) = (AnalogUnits.Other, "kPa");
            (input.Volts1, input.Value1, input.Volts2, input.Value2) = (0.5m, -20.125m, 4.5m, 999999.999m);
            await db.SaveChangesAsync(Cancel);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var input = await db.AnalogInputs.AsNoTracking().SingleAsync(i => i.Channel == 1, Cancel);
            Assert.Equal((AnalogUnits.Other, "kPa", 0.5m, -20.125m, 4.5m, 999999.999m), (input.Unit, input.UnitText, input.Volts1, input.Value1, input.Volts2, input.Value2));
            Assert.True(input.HasScale);
        }
    }

    // What the handler checks, the database refuses too: half a scale, a unit it doesn't know, a channel twice.
    [Fact]
    public async Task Should_RefuseHalfAScale_AnUnknownUnit_AndAChannelTwice()
    {
        await using var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(Cancel);
        await database.Services.SeedBoardAsync(new BoardCapabilities(1, 0, true, FourInputs), Cancel);

        await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(database, i => (i.Volts1, i.Value1) = (0m, 0m)));
        await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(database, i => i.Unit = "F"));
        await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(database, i => i.Channel = 2));
        await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(database, i => i.Channel = 0));
    }

    [Fact]
    public async Task Should_ListTheAnalogInputs_EveryRunningBoardHas()
    {
        await using var database = await DatabaseAsync(analog: new AnalogAbility(2, 1023, 5m), seeded: 4);
        var barn = await AddBoardRowAsync(database, "Barn");
        await database.Services.SeedBoardAsync(barn, new BoardCapabilities(2, 0, true, new AnalogAbility(1, 1023, 3.3m)), Cancel);
        var stopped = await AddBoardRowAsync(database, "Shed");
        await database.Services.SeedBoardAsync(stopped, new BoardCapabilities(2, 0, true, new AnalogAbility(1, 1023, 3.3m)), Cancel);
        var barnBoard = new FakeRelayBoard(relayCount: 2, inputCount: 0, analog: new AnalogAbility(1, 1023, 3.3m));
        await database.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(barnBoard).Build(barn), Cancel);

        var inputs = await Handler(database).ListAsync(Cancel);

        // Board 1's inputs 3 and 4 are left from a board with more; Shed doesn't run.
        Assert.Equal([(1, 1), (1, 2), (barn, 1)], inputs.Select(i => (i.BoardId, i.Channel)));
        Assert.Equal(AnalogScale.None, inputs[0].Scale);
        Assert.Equal("Analog 1", inputs[2].Name);
        Assert.Null(await Handler(database).GetAsync(await IdAsync(database, 1, 3), Cancel));
        Assert.Null(await Handler(database).GetAsync(await IdAsync(database, stopped, 1), Cancel));
        Assert.Null(await Handler(database).GetAsync(999, Cancel));
        Assert.Equal(barn, (await Handler(database).GetAsync(inputs[2].Id, Cancel))!.BoardId);
    }

    [Fact]
    public async Task Should_SaveTheSettings_RoundedToThreeDecimals_AndTellThePages()
    {
        await using var database = await DatabaseAsync();
        var changes = 0;
        database.Services.GetRequiredService<RelayChanges>().Changed += (_, _) => changes++;
        var id = await IdAsync(database, 1, 2);

        var saved = await Handler(database).SaveAsync(new AnalogInputSettings(id, " Paint shop ", false, AnalogUnits.Celsius, "ignored", 0.0004m, -20m, 5m, 80.0005m), Cancel);

        Assert.Null(saved);
        Assert.Equal(1, changes);
        var input = await Handler(database).GetAsync(id, Cancel);
        Assert.Equal(new AnalogInputView(id, 2, "Paint shop", false, new AnalogScale(AnalogUnits.Celsius, "", 0m, -20m, 5m, 80.001m)) { BoardId = 1 }, input);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.EventLog.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_SaveVolts_WithoutAScale_AndAnOtherUnit_WithItsText()
    {
        await using var database = await DatabaseAsync();
        var id = await IdAsync(database, 1, 1);

        Assert.Null(await Handler(database).SaveAsync(new AnalogInputSettings(id, "Pressure", true, AnalogUnits.Other, " kPa ", 0.5m, 0m, 4.5m, 600m), Cancel));
        Assert.Equal(new AnalogScale(AnalogUnits.Other, "kPa", 0.5m, 0m, 4.5m, 600m), (await Handler(database).GetAsync(id, Cancel))!.Scale);

        Assert.Null(await Handler(database).SaveAsync(new AnalogInputSettings(id, "Pressure", true, AnalogUnits.Volts, "kPa", null, null, null, null), Cancel));
        Assert.Equal(new AnalogScale(AnalogUnits.Volts, "", null, null, null, null), (await Handler(database).GetAsync(id, Cancel))!.Scale);

        Assert.Null(await Handler(database).SaveAsync(new AnalogInputSettings(id, "Pressure", true, AnalogUnits.None, "", null, null, null, null), Cancel));
    }

    [Theory]
    [InlineData("", AnalogUnits.None, "", null, null, null, null, "Enter a name.")]
    [InlineData("12345678901234567890123456789012345678901", AnalogUnits.None, "", null, null, null, null, "The name can be at most 40 characters.")]
    [InlineData("Heat", "F", "", "0", "0", "5", "100", "Choose a unit.")]
    [InlineData("Heat", AnalogUnits.Other, " ", "0", "0", "5", "100", "Enter the unit.")]
    [InlineData("Heat", AnalogUnits.Other, "123456789", "0", "0", "5", "100", "The unit can be at most 8 characters.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", null, null, null, null, "This unit needs a scale: fill in both points.")]
    [InlineData("Heat", AnalogUnits.Other, "kPa", null, null, null, null, "This unit needs a scale: fill in both points.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "0", "-20", "5", null, "Fill in both points of the scale, or none.")]
    [InlineData("Heat", AnalogUnits.None, "", null, null, "5", null, "Fill in both points of the scale, or none.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "-0.1", "-20", "5", "80", "A voltage must be from 0 to 100 V.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "0", "-20", "100.001", "80", "A voltage must be from 0 to 100 V.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "2.5", "-20", "2.5", "80", "The two points need different voltages.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "2.5", "-20", "2.5004", "80", "The two points need different voltages.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "0", "-1000000.001", "5", "80", "A value must be from -1,000,000 to 1,000,000.")]
    [InlineData("Heat", AnalogUnits.Bar, "", "0", "0", "5", "1000001", "A value must be from -1,000,000 to 1,000,000.")]
    [InlineData("Heat", AnalogUnits.Celsius, "", "0", "20", "5", "20", "The two points need different values.")]
    [InlineData("Heat", AnalogUnits.Volts, "", "0", "1.5", "5", "1.5004", "The two points need different values.")]
    public async Task Should_RefuseSettings_ThatDontWork(string name, string unit, string unitText, string? volts1, string? value1, string? volts2, string? value2, string problem)
    {
        await using var database = await DatabaseAsync();
        var changes = 0;
        database.Services.GetRequiredService<RelayChanges>().Changed += (_, _) => changes++;
        var id = await IdAsync(database, 1, 1);
        var settings = new AnalogInputSettings(id, name, true, unit, unitText, Number(volts1), Number(value1), Number(volts2), Number(value2));

        Assert.Equal(problem, await Handler(database).SaveAsync(settings, Cancel));
        Assert.Equal(0, changes);
        Assert.Equal("Analog 1", (await Handler(database).GetAsync(id, Cancel))!.Name);
    }

    [Fact]
    public async Task Should_RefuseToSave_AnAnalogInputItsBoardDoesntHave()
    {
        await using var database = await DatabaseAsync(analog: new AnalogAbility(1, 1023, 5m), seeded: 2);

        var problem = await Handler(database).SaveAsync(new AnalogInputSettings(await IdAsync(database, 1, 2), "Gone", true, AnalogUnits.None, "", null, null, null, null), Cancel);

        Assert.Equal("The analog input doesn't exist.", problem);
        Assert.Equal("The analog input doesn't exist.", await Handler(database).SaveAsync(new AnalogInputSettings(999, "Gone", true, AnalogUnits.None, "", null, null, null, null), Cancel));
    }

    private static decimal? Number(string? text)
    {
        return text is null ? null : decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AnalogInputsHandler Handler(TestDatabase database)
    {
        return database.Services.GetRequiredService<AnalogInputsHandler>();
    }

    // The app's board with analog inputs, its rows seeded for seeded of them (the board's count without).
    private static async Task<TestDatabase> DatabaseAsync(AnalogAbility? analog = null, int? seeded = null)
    {
        analog ??= FourInputs;
        var database = TestDatabase.Create((services, configuration) =>
        {
            services.AddLocalClock(configuration);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
            services.AddBoards(configuration);
            services.AddEvents(configuration);
            services.AddRelays(configuration);
            services.AddGroups(configuration);
            services.AddInputs(configuration);
        });
        await database.Services.InitializeDatabaseAsync(relayCount: 2, inputCount: 0, Cancel);
        await database.Services.SeedBoardAsync(new BoardCapabilities(2, 0, true, new AnalogAbility(seeded ?? analog.Count, analog.MaxCounts, analog.FullScaleVolts)), Cancel);
        await database.Services.UseFakeBoardAsync(new FakeRelayBoard(relayCount: 2, inputCount: 0, analog: analog), seed: false);
        return database;
    }

    private static async Task<int> AddBoardRowAsync(TestDatabase database, string name)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        var row = new Board { Name = name, SortOrder = await db.Boards.CountAsync(Cancel) + 1 };
        db.Boards.Add(row);
        await db.SaveChangesAsync(Cancel);
        return row.Id;
    }

    private static async Task<int> IdAsync(TestDatabase database, int boardId, int channel)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        return await db.AnalogInputs.Where(i => i.BoardId == boardId && i.Channel == channel).Select(i => i.Id).SingleAsync(Cancel);
    }

    private static async Task RenameAsync(TestDatabase database, int boardId, int channel, string name)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        (await db.AnalogInputs.SingleAsync(i => i.BoardId == boardId && i.Channel == channel, Cancel)).Name = name;
        await db.SaveChangesAsync(Cancel);
    }

    // Changes analog input 1 of board 1 straight in the database, past the handler.
    private static async Task ChangeAsync(TestDatabase database, Action<AnalogInput> change)
    {
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        change(await db.AnalogInputs.SingleAsync(i => i.BoardId == 1 && i.Channel == 1, Cancel));
        await db.SaveChangesAsync(Cancel);
    }
}
