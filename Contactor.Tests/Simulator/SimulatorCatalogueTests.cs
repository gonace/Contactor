using Contactor.Simulator;

namespace Contactor.Tests.Simulator;

/// <summary>What "Add a board" shows of each model, and the port it offers for the model chosen in it.</summary>
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
    [InlineData("denkovi-smartden-ip-16r-mt", 8502)]
    [InlineData("denkovi-daenetip2-v2-16", 8161)]
    [InlineData("denkovi-daenetip2-v2-8", 8161)]
    [InlineData("denkovi-daenetip2-v2-2", 8161)]
    [InlineData("devantech-eth1610", 17494)]
    [InlineData("advantech-adam-6060", 8502)]
    [InlineData("advantech-adam-6066", 8502)]
    [InlineData("advantech-adam-6260", 8502)]
    [InlineData("shelly-1", 8180)]
    [InlineData("shelly-4", 8180)]
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

    [Fact]
    public void Should_ListTheManufacturers_InAlphabeticalOrder_WithTheirModelCounts()
    {
        var manufacturers = SimulatorCatalogue.Manufacturers;

        Assert.Equal(["Advantech", "Denkovi", "Devantech", "Shelly", "Velleman", "Waveshare"], manufacturers.Select(m => m.Name));
        Assert.Equal([3, 4, 5, 4, 3, 5], manufacturers.Select(m => m.Models.Count));
        Assert.Equal(SimulatorCatalogue.All.Count, manufacturers.Sum(m => m.Models.Count));
        Assert.All(manufacturers, m => Assert.All(m.Models, model => Assert.Equal(m.Name, model.Model.Brand)));
    }

    [Fact]
    public void Should_ListAManufacturersModels_InAlphabeticalOrder_WithNumbersAsNumbers()
    {
        string[] Names(string manufacturer) =>
            [.. SimulatorCatalogue.Manufacturers.Single(m => m.Name == manufacturer).Models.Select(m => m.Model.ModelName)];

        Assert.Equal(["VM201", "VM204", "VM208"], Names("Velleman"));
        Assert.Equal(["ADAM-6060", "ADAM-6066", "ADAM-6260"], Names("Advantech"));
        Assert.Equal(["ETH002-B", "ETH008-B", "ETH484-B", "ETH1610", "ETH8020-B"], Names("Devantech"));
        Assert.Equal(["DAEnetIP2 v2 2-relay kit", "DAEnetIP2 v2 8-relay kit", "DAEnetIP2 v2 16-relay kit", "smartDEN IP-16R-MT"], Names("Denkovi"));
        Assert.Equal(["Gen2+ 1", "Gen2+ 2", "Gen2+ 3", "Gen2+ 4"], Names("Shelly"));
        Assert.Equal(
            ["Modbus PoE ETH Relay", "Modbus PoE ETH Relay (B)", "Modbus PoE ETH Relay (C)", "Modbus PoE ETH Relay 16CH", "Modbus PoE ETH Relay 30CH"],
            Names("Waveshare"));
    }

    [Theory]
    [InlineData("vm201", "TCP packets, pushes changes", "9760", "Own rules: timers per relay (the app reads them and can turn them off). Pulse 1 s – 99 h.")]
    [InlineData("vm204", "HTTP, polled every 2 s", "80 → 8204", "No own rules. Pulse 1 s – 2147483 s.")]
    [InlineData("vm208", "HTTP, polled every 2 s", "80 → 8208", "Own rules: a week schedule per relay (the app reads them and can turn them off; there may be others it can't see). No pulse.")]
    [InlineData("waveshare-8", "Modbus RTU over TCP, polled every 2 s", "4196", "No own rules. Pulse 0.1 s – 3276.7 s.")]
    [InlineData("waveshare-8b", "Modbus RTU over TCP, polled every 2 s", "4196", "Own rules: relay modes that follow the inputs (the app reads them and can turn them off). Pulse 0.1 s – 3276.7 s.")]
    [InlineData("devantech-eth008", "TCP commands, polled every 2 s", "17494", "Own rules: the app can't tell whether it has any. Pulse 0.1 s – 25.5 s.")]
    [InlineData("denkovi-daenetip2-v2-8", "SNMP over UDP, polled every 2 s", "161 → 8161", "Own rules: the app can't tell whether it has any. No pulse.")]
    [InlineData("advantech-adam-6060", "Modbus TCP, polled every 2 s", "502 → 8502", "Own rules: the app can't tell whether it has any. No pulse.")]
    [InlineData(
        "shelly-2",
        "HTTP JSON-RPC, polled every 2 s",
        "80 → 8180",
        "Own rules: schedules and auto-on (the app reads them and can turn them off; there may be others it can't see). Power-on state per relay. No pulse.")]
    public void Should_DescribeAModel_FromTheCatalogueAndItsCapabilities(string type, string talks, string port, string rules)
    {
        var model = SimulatorCatalogue.Find(type)!;

        Assert.Equal((talks, port, rules), (model.Talks, model.PortText, model.Rules));
    }

    [Fact]
    public void Should_SayHowEveryModelIsTalkedTo_AndNameTheRulesTheAppReads()
    {
        Assert.All(SimulatorCatalogue.All, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Protocol), model.Model.Id);
            Assert.True(model.Model.Capabilities.ReportsChanges || model.PollSeconds >= 1, model.Model.Id);
            Assert.True(!model.Model.Capabilities.OwnRulesOrCantTell.Reads || model.OwnRulesName is not null, model.Model.Id);
        });
    }

    [Theory]
    [InlineData("vm201", SimulatorLoginFields.UsernameAndPassword, "At most 8 characters each. Empty: the board asks for no login.")]
    [InlineData("vm204", SimulatorLoginFields.UsernameAndPassword, "At most 31 characters each, 48 together. Empty: the board asks for no login.")]
    [InlineData("vm208", SimulatorLoginFields.None, "The board takes no login.")]
    [InlineData("waveshare-8", SimulatorLoginFields.None, "The board takes no login.")]
    [InlineData("advantech-adam-6060", SimulatorLoginFields.None, "The board takes no login.")]
    [InlineData("shelly-2", SimulatorLoginFields.Password, "A password only, at most 128 characters. Empty: the board asks for no password.")]
    [InlineData("devantech-eth008", SimulatorLoginFields.Password, "A password only, at most 32 characters. Empty: the board asks for no password.")]
    [InlineData("denkovi-daenetip2-v2-8", SimulatorLoginFields.Password, "Its read-write community, 4 to 12 characters. Empty: the factory one, private.")]
    public void Should_OfferTheModelsOwnLoginFields_WithItsLimits(string type, SimulatorLoginFields fields, string text)
    {
        var model = SimulatorCatalogue.Find(type)!;

        Assert.Equal((fields, text), (model.LoginFields, model.LoginText));
    }
}
