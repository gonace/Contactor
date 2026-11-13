using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
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
    [InlineData("devantech-eth002")]
    [InlineData("devantech-eth008")]
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

    // Checked when the board is built, so a wrong setting fails where it is chosen, not later on the connection.
    [Fact]
    public void Should_RefuseToBuild_WithSettingsTheBoardCannotTake()
    {
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Build("vm204", settings: new() { ["Vm204:PollSeconds"] = "0" }));
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
        Assert.Equal(["Devantech", "Velleman", "Waveshare"], BoardCatalogue.Brands.Select(m => m.Name));
        Assert.Equal(
            ["VM201", "VM204", "VM208"],
            BoardCatalogue.Brands.Single(m => m.Name == "Velleman").Models.Select(m => m.ModelName));
        Assert.Equal(
            BoardCatalogue.All.Select(r => r.Model).OrderBy(m => m.Brand, StringComparer.OrdinalIgnoreCase),
            BoardCatalogue.Brands.SelectMany(m => m.Models));
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
        return registration.Create(registration.Model, app, configuration);
    }
}
