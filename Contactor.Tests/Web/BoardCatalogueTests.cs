using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>Building each model of board in the catalogue, as the app does when it is chosen.</summary>
public sealed class BoardCatalogueTests
{
    [Theory]
    [InlineData("vm201")]
    [InlineData(" VM201 ")]
    public async Task Should_BuildTheVm201_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Same(Vm201Model.Model, board.Model);
        Assert.IsType<Vm201Board>(board.Board);
        Assert.IsType<Vm201Connection>(board.Connection);
        Assert.IsType<Vm201Traffic>(board.Traffic);
    }

    [Theory]
    [InlineData("vm204")]
    [InlineData(" VM204")]
    public async Task Should_BuildTheVm204_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Same(Vm204Model.Model, board.Model);
        Assert.IsType<Vm204Board>(board.Board);
        Assert.IsType<Vm204Connection>(board.Connection);
        Assert.IsType<Vm204Traffic>(board.Traffic);
    }

    [Theory]
    [InlineData("vm208")]
    [InlineData("VM208 ")]
    public async Task Should_BuildTheVm208_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Same(Vm208Model.Model, board.Model);
        Assert.IsType<Vm208Board>(board.Board);
        Assert.IsType<Vm208Connection>(board.Connection);
        Assert.IsType<Vm208Traffic>(board.Traffic);
    }

    [Theory]
    [InlineData("waveshare-8")]
    [InlineData("waveshare-8b")]
    [InlineData("Waveshare-8C")]
    [InlineData("waveshare-16")]
    [InlineData(" waveshare-30")]
    public async Task Should_BuildEachWaveshareModel_WhenChosen(string type)
    {
        await using var board = Build(type);

        var model = board.Model;
        Assert.Contains(model, WaveshareModels.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), model.Id);
        var waveshare = Assert.IsType<WaveshareBoard>(board.Board);
        Assert.Same(model.Capabilities, waveshare.Capabilities);
        Assert.Same(model, board.Services.GetRequiredService<WaveshareClient>().Model);
        Assert.IsType<WaveshareConnection>(board.Connection);
        Assert.IsType<WaveshareTraffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(4196, board.Connection.Endpoint.Port);
    }

    [Theory]
    [InlineData("denkovi-smartden-ip-16r-mt")]
    [InlineData(" DENKOVI-SMARTDEN-IP-16R-MT ")]
    public async Task Should_BuildTheSmartDenModel_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Same(SmartDenModels.Ip16RMt, board.Model);
        var smartDen = Assert.IsType<SmartDenBoard>(board.Board);
        Assert.Same(board.Model.Capabilities, smartDen.Capabilities);
        Assert.Same(board.Model, board.Services.GetRequiredService<SmartDenClient>().Model);
        Assert.IsType<SmartDenConnection>(board.Connection);
        Assert.IsType<SmartDenTraffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(502, board.Connection.Endpoint.Port);
    }

    [Theory]
    [InlineData("denkovi-daenetip2-v2-16")]
    [InlineData("Denkovi-DAEnetIP2-v2-8")]
    [InlineData(" denkovi-daenetip2-v2-2 ")]
    public async Task Should_BuildEachDaenetIp2Kit_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Contains(board.Model, DaenetIp2Models.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), board.Model.Id);
        var daenet = Assert.IsType<DaenetIp2Board>(board.Board);
        Assert.Same(board.Model.Capabilities, daenet.Capabilities);
        Assert.Same(board.Model, board.Services.GetRequiredService<DaenetIp2Client>().Model);
        Assert.IsType<DaenetIp2Connection>(board.Connection);
        Assert.IsType<DaenetIp2Traffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(161, board.Connection.Endpoint.Port);
        Assert.Equal(BoardLogin.None, board.Connection.Login);
    }

    [Theory]
    [InlineData("devantech-eth002")]
    [InlineData("devantech-eth008")]
    [InlineData("Devantech-ETH1610")]
    [InlineData("Devantech-ETH484")]
    [InlineData(" devantech-eth8020 ")]
    public async Task Should_BuildEachDevantechModel_WhenChosen(string type)
    {
        await using var board = Build(type);

        Assert.Contains(board.Model, DevantechModels.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), board.Model.Id);
        var devantech = Assert.IsType<DevantechBoard>(board.Board);
        Assert.Same(board.Model.Capabilities, devantech.Capabilities);
        Assert.Same(board.Model, board.Services.GetRequiredService<DevantechClient>().Model);
        Assert.IsType<DevantechConnection>(board.Connection);
        Assert.IsType<DevantechTraffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(17494, board.Connection.Endpoint.Port);
    }

    [Fact]
    public async Task Should_BuildTheVm208_WithoutAnAddress()
    {
        await using var board = Build("vm208", vm208Host: "");

        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal("", board.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Vm208Options>>().Value.Host);
    }

    [Theory]
    [InlineData("advantech-adam-6060")]
    [InlineData("ADVANTECH-ADAM-6066 ")]
    [InlineData("advantech-adam-6260")]
    public async Task Should_BuildEachAdamModel(string type)
    {
        await using var board = Build(type);

        Assert.Contains(board.Model, AdamModels.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), board.Model.Id);
        var adam = Assert.IsType<AdamBoard>(board.Board);
        Assert.Same(board.Model.Capabilities, adam.Capabilities);
        Assert.Same(board.Model, board.Services.GetRequiredService<AdamClient>().Model);
        Assert.IsType<AdamConnection>(board.Connection);
        Assert.IsType<AdamTraffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(502, board.Connection.Endpoint.Port);
    }

    [Theory]
    [InlineData("shelly-1", 1)]
    [InlineData("Shelly-2", 2)]
    [InlineData(" shelly-3", 3)]
    [InlineData("SHELLY-4 ", 4)]
    public async Task Should_BuildEachShellyEntry_WhenChosen(string type, int relays)
    {
        await using var board = Build(type, settings: new() { ["Shelly:Password"] = "quince7" });

        Assert.Contains(board.Model, ShellyModels.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), board.Model.Id);
        var shelly = Assert.IsType<ShellyBoard>(board.Board);
        Assert.Equal(relays, shelly.Capabilities.RelayCount);
        Assert.Same(board.Model, board.Services.GetRequiredService<ShellyClient>().Model);
        Assert.IsType<ShellyConnection>(board.Connection);
        Assert.IsType<ShellyTraffic>(board.Traffic);
        Assert.False(board.Connection.Endpoint.HasHost);
        Assert.Equal(80, board.Connection.Endpoint.Port);
        Assert.Equal(new BoardLogin("", "quince7"), board.Connection.Login);
    }

    // Checked when the board is built, so a wrong setting fails where it is chosen, not later on the connection.
    [Fact]
    public void Should_RefuseToBuild_WithSettingsTheBoardCannotTake()
    {
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Build("vm204", settings: new() { ["Vm204:PollSeconds"] = "0" }));
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Build("shelly-1", settings: new() { ["Shelly:Password"] = new string('x', ShellyApi.PasswordMaxLength + 1) }));
    }

    [Fact]
    public void Should_ListEachModelOnce_InLowerCase()
    {
        var ids = BoardCatalogue.All.Select(r => r.Model.Id).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
        Assert.NotNull(BoardCatalogue.Find(BoardCatalogue.EarlierDefaultType));
    }

    // The setup guide asks for the brand, then the model: every model is under its brand, once, in the catalogue's order.
    [Fact]
    public void Should_ListTheBrands_InAlphabeticalOrder_EachWithItsModels()
    {
        Assert.Equal(["Advantech", "Denkovi", "Devantech", "Shelly", "Velleman", "Waveshare"], BoardCatalogue.Brands.Select(m => m.Name));
        Assert.Equal(
            ["VM201", "VM204", "VM208"],
            BoardCatalogue.Brands.Single(m => m.Name == "Velleman").Models.Select(m => m.ModelName));
        Assert.Equal(
            ["Gen2+ 1", "Gen2+ 2", "Gen2+ 3", "Gen2+ 4"],
            BoardCatalogue.Brands.Single(m => m.Name == "Shelly").Models.Select(m => m.ModelName));
        Assert.Equal(
            BoardCatalogue.All.Select(r => r.Model).OrderBy(m => m.Brand, StringComparer.OrdinalIgnoreCase),
            BoardCatalogue.Brands.SelectMany(m => m.Models));
    }

    // Every model states what the app can know about its own rules, so a new model has to choose: none is taken from
    // the default, which would quietly mean "can't tell".
    [Fact]
    public void Should_StateEachModelsOwnRulesAbility()
    {
        var none = OwnRulesAbility.None;
        var cantTell = OwnRulesAbility.CantTell;
        var reads = OwnRulesAbility.ReadsAndTurnsOff;
        var readsMayHaveUnseen = new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true);
        Dictionary<string, OwnRulesAbility> expected = new()
        {
            ["vm201"] = reads,
            ["vm204"] = none,
            ["vm208"] = reads,
            ["waveshare-8"] = none,
            ["waveshare-8b"] = reads,
            ["waveshare-8c"] = reads,
            ["waveshare-16"] = none,
            ["waveshare-30"] = none,
            ["denkovi-smartden-ip-16r-mt"] = cantTell,
            ["denkovi-daenetip2-v2-16"] = cantTell,
            ["denkovi-daenetip2-v2-8"] = cantTell,
            ["denkovi-daenetip2-v2-2"] = cantTell,
            ["devantech-eth002"] = cantTell,
            ["devantech-eth008"] = cantTell,
            ["devantech-eth1610"] = cantTell,
            ["devantech-eth484"] = cantTell,
            ["devantech-eth8020"] = cantTell,
            ["advantech-adam-6060"] = cantTell,
            ["advantech-adam-6066"] = cantTell,
            ["advantech-adam-6260"] = cantTell,
            ["shelly-1"] = readsMayHaveUnseen,
            ["shelly-2"] = readsMayHaveUnseen,
            ["shelly-3"] = readsMayHaveUnseen,
            ["shelly-4"] = readsMayHaveUnseen,
        };

        Assert.Equal(
            expected.OrderBy(e => e.Key, StringComparer.Ordinal),
            BoardCatalogue.All.Select(r => KeyValuePair.Create(r.Model.Id, r.Model.Capabilities.OwnRules!)).OrderBy(e => e.Key, StringComparer.Ordinal));
    }

    // Only the Shelly devices say what their relays do after a power cut; every other model leaves it at "can't tell"
    // until its own issue (the smartDEN's and the DAEnetIP2 v2's "Save Outputs": Issue #514 and Issue #515).
    [Fact]
    public void Should_StateThePowerOnAbility_OfTheShellyDevicesOnly()
    {
        Assert.Equal(
            ["shelly-1", "shelly-2", "shelly-3", "shelly-4"],
            BoardCatalogue.All.Where(r => r.Model.Capabilities.PowerOn is not null).Select(r => r.Model.Id).Order(StringComparer.Ordinal));
        Assert.All(
            BoardCatalogue.All.Where(r => r.Model.Capabilities.PowerOn is not null),
            r => Assert.True(r.Model.Capabilities.PowerOn!.PerRelay));
    }

    // The brand isn't repeated in the model's name, and the full name still reads as people know it.
    [Fact]
    public void Should_NameEachModel_WithoutItsBrand()
    {
        Assert.All(BoardCatalogue.All, r =>
        {
            Assert.False(r.Model.ModelName.StartsWith(r.Model.Brand, StringComparison.OrdinalIgnoreCase), r.Model.Id);
            Assert.Equal($"{r.Model.Brand} {r.Model.ModelName}", r.Model.Name);
        });
        Assert.Equal("Waveshare Modbus PoE ETH Relay 30CH", BoardCatalogue.Find("waveshare-30")!.Model.Name);
        Assert.Equal("Denkovi DAEnetIP2 v2 16-relay kit", BoardCatalogue.Find("denkovi-daenetip2-v2-16")!.Model.Name);
        Assert.Equal("Shelly Gen2+ 2", BoardCatalogue.Find("shelly-2")!.Model.Name);
    }

    private static ActiveBoard Build(string type, string vm208Host = "127.0.0.1", Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["Vm201:Host"] = "127.0.0.1", ["Vm204:Host"] = "127.0.0.1", ["Vm208:Host"] = vm208Host };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var app = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(TimeProvider.System)

            // What only some boards have logs events: built, but never used here.
            .AddDbContextFactory<ContactorDbContext>(options => options.UseSqlite("Data Source=unused.db"))
            .BuildServiceProvider();
        var registration = BoardCatalogue.Find(type) ?? throw new ArgumentException($"No board \"{type}\".", nameof(type));
        return registration.Create(registration.Model, new BoardSetup(BoardId: 1, IsFirst: true, Endpoint: null, Login: null), app, configuration);
    }
}
