using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Boards.Vm204;
using Contactor.Boards.Vm208;
using Contactor.Boards.Waveshare;
using Contactor.Web.Features.Boards;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>Choosing the board model with <c>Board:Type</c>.</summary>
public sealed class BoardCatalogueTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("vm201")]
    [InlineData(" VM201 ")]
    public void Should_RegisterTheVm201_WhenChosenOrNotSet(string? type)
    {
        using var services = Build(type);

        Assert.Same(Vm201Model.Model, services.GetRequiredService<BoardModel>());
        Assert.IsType<Vm201Board>(services.GetRequiredService<IRelayBoard>());
        Assert.IsType<Vm201Connection>(services.GetRequiredService<IBoardConnection>());
        Assert.IsType<Vm201Traffic>(services.GetRequiredService<IBoardTraffic>());
    }

    [Theory]
    [InlineData("vm204")]
    [InlineData(" VM204")]
    public void Should_RegisterTheVm204_WhenChosen(string type)
    {
        using var services = Build(type);

        Assert.Same(Vm204Model.Model, services.GetRequiredService<BoardModel>());
        Assert.IsType<Vm204Board>(services.GetRequiredService<IRelayBoard>());
        Assert.IsType<Vm204Connection>(services.GetRequiredService<IBoardConnection>());
        Assert.IsType<Vm204Traffic>(services.GetRequiredService<IBoardTraffic>());
    }

    [Theory]
    [InlineData("vm208")]
    [InlineData("VM208 ")]
    public void Should_RegisterTheVm208_WhenChosen(string type)
    {
        using var services = Build(type);

        Assert.Same(Vm208Model.Model, services.GetRequiredService<BoardModel>());
        Assert.IsType<Vm208Board>(services.GetRequiredService<IRelayBoard>());
        Assert.IsType<Vm208Connection>(services.GetRequiredService<IBoardConnection>());
        Assert.IsType<Vm208Traffic>(services.GetRequiredService<IBoardTraffic>());
    }

    [Theory]
    [InlineData("waveshare-8")]
    [InlineData("waveshare-8b")]
    [InlineData("Waveshare-8C")]
    [InlineData("waveshare-16")]
    [InlineData(" waveshare-30")]
    public void Should_RegisterEachWaveshareModel_WhenChosen(string type)
    {
        using var services = Build(type);

        var model = services.GetRequiredService<BoardModel>();
        Assert.Contains(model, WaveshareModels.All);
        Assert.Equal(type.Trim().ToLowerInvariant(), model.Id);
        var board = Assert.IsType<WaveshareBoard>(services.GetRequiredService<IRelayBoard>());
        Assert.Same(model.Capabilities, board.Capabilities);
        Assert.Same(model, services.GetRequiredService<WaveshareClient>().Model);
        Assert.IsType<WaveshareConnection>(services.GetRequiredService<IBoardConnection>());
        Assert.IsType<WaveshareTraffic>(services.GetRequiredService<IBoardTraffic>());
        Assert.False(services.GetRequiredService<IBoardConnection>().Endpoint.HasHost);
        Assert.Equal(4196, services.GetRequiredService<IBoardConnection>().Endpoint.Port);
    }

    [Fact]
    public void Should_BuildTheVm208_WithoutAnAddress()
    {
        using var services = Build("vm208", vm208Host: "");

        Assert.False(services.GetRequiredService<IBoardConnection>().Endpoint.HasHost);
        Assert.Equal("", services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Vm208Options>>().Value.Host);
    }

    [Theory]
    [InlineData("vm209")]
    [InlineData("")]
    public void Should_RefuseToStart_WithABoardItDoesNotKnow(string type)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(type));

        Assert.Equal($"Board:Type is \"{type}\", which isn't a board the app knows. Use one of: vm201, vm204, vm208, waveshare-8, waveshare-8b, waveshare-8c, waveshare-16, waveshare-30.", error.Message);
    }

    [Fact]
    public void Should_ListEachModelOnce_InLowerCase()
    {
        var ids = BoardCatalogue.All.Select(r => r.Model.Id).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
        Assert.NotNull(BoardCatalogue.Find(BoardCatalogue.DefaultType));
    }

    private static ServiceProvider Build(string? type, string vm208Host = "127.0.0.1")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Board:Type"] = type, ["Vm201:Host"] = "127.0.0.1", ["Vm204:Host"] = "127.0.0.1", ["Vm208:Host"] = vm208Host })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBoards(configuration);
        return services.BuildServiceProvider();
    }
}
