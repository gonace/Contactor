using Contactor.Simulator;

namespace Contactor.Tests.Simulator;

/// <summary>The port the "Add a board" form offers for the model chosen in it.</summary>
public sealed class SimulatorCatalogueTests
{
    [Theory]
    [InlineData("vm201", 9760)]
    [InlineData("vm204", 8204)]
    [InlineData("vm208", 8208)]
    [InlineData("waveshare-8", 4196)]
    [InlineData("waveshare-8b", 4196)]
    [InlineData("waveshare-8c", 4196)]
    [InlineData("waveshare-16", 4196)]
    [InlineData("waveshare-30", 4196)]
    public void Should_OfferTheModelsOwnPort_WhenItIsFree(string type, int port)
    {
        Assert.Equal(port, SimulatorCatalogue.FreePort(type, [9761, 4197]));
    }

    [Fact]
    public void Should_OfferTheFirstFreePortAfterIt_WhenItIsTaken()
    {
        Assert.Equal(4197, SimulatorCatalogue.FreePort("waveshare-8", [4196]));
        Assert.Equal(9762, SimulatorCatalogue.FreePort("vm201", [9760, 9761, 9763]));
    }

    [Fact]
    public void Should_OfferThePortOfTheModelNowChosen()
    {
        // The same boards running, another model chosen: the form follows the model.
        int[] taken = [9760, 4196];

        Assert.Equal(9761, SimulatorCatalogue.FreePort("vm201", taken));
        Assert.Equal(4197, SimulatorCatalogue.FreePort("waveshare-8c", taken));
        Assert.Equal(8204, SimulatorCatalogue.FreePort("vm204", taken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vm209")]
    public void Should_OfferTheFirstModelsPort_ForAnUnknownModel(string? type)
    {
        Assert.Equal(SimulatorCatalogue.All[0].Port, SimulatorCatalogue.FreePort(type, []));
    }

    [Fact]
    public void Should_StopAtTheLastPort()
    {
        Assert.Equal(65535, SimulatorCatalogue.FreePort("vm201", [.. Enumerable.Range(9760, 65535 - 9760)]));
        Assert.Null(SimulatorCatalogue.FreePort("vm201", [.. Enumerable.Range(9760, 65535 - 9760 + 1)]));
    }
}
