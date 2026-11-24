using System.Globalization;
using Contactor.Simulator;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Simulator;

/// <summary>The page's "Add a board" panel: manufacturer, model, port and login, and the board it adds.</summary>
public sealed class AddBoardFormTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

    private readonly SimulatedBoards _boards = new(NullLoggerFactory.Instance, new FixedTimeProvider(Now), Options.Create(new SimulatorOptions()));

    public async ValueTask DisposeAsync()
    {
        await _boards.DisposeAsync();
    }

    // The demo gives each visitor's simulator its own ports (Issue #606): a board opens only there.
    [Fact]
    public async Task Should_OfferAndTakeOnlyAPortInItsBlock_WhenItHasOne()
    {
        var port = SimulatedBoardsTests.FreePort();
        var block = string.Create(CultureInfo.InvariantCulture, $"{port}-{port}");
        await using var boards = new SimulatedBoards(
            NullLoggerFactory.Instance, new FixedTimeProvider(Now), Options.Create(new SimulatorOptions { Ports = block }));
        var form = new AddBoardForm(boards);
        form.Open();
        Assert.Equal(port.ToString(CultureInfo.InvariantCulture), form.Port);

        form.Port = "9760";
        Assert.Null(form.Add());
        Assert.Equal($"Choose a port from {port} to {port}.", form.Error);
        Assert.Empty(boards.Boards);

        form.Port = port.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(port, form.Add());

        // Its one port is taken now: nothing more to offer.
        form.Open();
        Assert.Equal("", form.Port);
    }

    [Fact]
    public void Should_Open_WithTheFirstModelAndItsPort()
    {
        var form = new AddBoardForm(_boards);
        Assert.False(form.IsOpen);

        form.Open();

        Assert.True(form.IsOpen);
        Assert.Equal(("Velleman", "vm201", "9760"), (form.Manufacturer.Name, form.Model.Model.Id, form.Port));
        Assert.Equal("Add Velleman VM201 on port 9760", form.AddText);
        Assert.Null(form.PortNote);
    }

    [Fact]
    public void Should_ChooseAManufacturersFirstModel_WithItsPort_AndNoLogin()
    {
        var form = new AddBoardForm(_boards);
        form.Open();
        form.Username = "john";
        form.Password = "secret";

        form.ChooseManufacturer("Shelly");

        Assert.Equal(("Shelly", "shelly-1", "8180"), (form.Manufacturer.Name, form.Model.Model.Id, form.Port));
        Assert.Equal(("", ""), (form.Username, form.Password));

        form.ChooseModel("shelly-2");
        Assert.Equal("Add Shelly Gen2+ 2 on port 8180", form.AddText);

        // Only the catalogue's manufacturers, and only the chosen manufacturer's models.
        form.ChooseManufacturer("Acme");
        form.ChooseManufacturer(null);
        form.ChooseModel("vm201");
        form.ChooseModel(null);
        Assert.Equal(("Shelly", "shelly-2"), (form.Manufacturer.Name, form.Model.Model.Id));
    }

    [Fact]
    public void Should_AddTheBoardAsTyped_AndClose()
    {
        var form = new AddBoardForm(_boards);
        var port = SimulatedBoardsTests.FreePort();
        form.Open();
        form.ChooseModel("vm204");
        form.Port = $" {port.ToString(CultureInfo.InvariantCulture)} ";
        form.Username = " admin ";
        form.Password = "VM204";
        Assert.Equal($"Add Velleman VM204 on port {port}", form.AddText);

        Assert.Equal(port, form.Add());

        Assert.False(form.IsOpen);
        Assert.Null(form.Error);
        var board = Assert.Single(_boards.Boards);
        Assert.Equal(("vm204", port, "admin"), (board.Board.Model.Id, board.Board.Port, board.Username));
        Assert.Equal(("", ""), (form.Username, form.Password));
    }

    [Fact]
    public void Should_RefuseATakenPort_WithTheBoardsName()
    {
        var port = SimulatedBoardsTests.FreePort();
        Assert.Null(_boards.Add("vm201", port, null, null));
        var form = new AddBoardForm(_boards);
        form.Open();
        form.ChooseManufacturer("Shelly");
        form.Port = port.ToString(CultureInfo.InvariantCulture);

        Assert.Equal($"{port} is taken by VM201.", form.PortNote);
        Assert.Null(form.Add());

        Assert.Equal($"Port {port} is taken by VM201.", form.Error);
        Assert.True(form.IsOpen);
        Assert.Single(_boards.Boards);
    }

    [Theory]
    [InlineData("", "", "", "The port is a number from 1 to 65535.")]
    [InlineData("x", "", "", "The port is a number from 1 to 65535.")]
    [InlineData("0", "", "", "The port is a number from 1 to 65535.")]
    [InlineData("65536", "", "", "The port is a number from 1 to 65535.")]
    [InlineData("-1", "", "", "The port is a number from 1 to 65535.")]
    [InlineData(null, "", "secret", "Write the user name too, or leave both empty.")]
    [InlineData(null, "johnjohnj", "secret", "The user name can be at most 8 characters on the relay board.")]
    public void Should_KeepTheChecksItHad(string? port, string username, string password, string error)
    {
        var form = new AddBoardForm(_boards);
        form.Open();
        form.Port = port ?? SimulatedBoardsTests.FreePort().ToString(CultureInfo.InvariantCulture);
        form.Username = username;
        form.Password = password;

        Assert.Null(form.Add());

        Assert.Contains(error, form.Error, StringComparison.Ordinal);
        Assert.True(form.IsOpen);
        Assert.Empty(_boards.Boards);
    }

    [Fact]
    public void Should_LeaveThePortOutOfTheButton_WhileItIsntOne()
    {
        var form = new AddBoardForm(_boards);
        form.Open();
        form.Port = "97x";

        Assert.Equal("Add Velleman VM201", form.AddText);
        Assert.Null(form.PortNote);
    }

    [Fact]
    public void Should_Close_OnCancel_KeepingNothingTyped()
    {
        var form = new AddBoardForm(_boards);
        form.Open();
        form.Username = "john";
        form.Password = "secret";
        form.Port = "x";
        _ = form.Add();

        form.Cancel();

        Assert.False(form.IsOpen);
        Assert.Equal(("", "", null), (form.Username, form.Password, form.Error));
        Assert.Empty(_boards.Boards);

        // Opened again, it starts afresh.
        form.ChooseManufacturer("Shelly");
        form.Open();
        Assert.Equal(("Velleman", "9760"), (form.Manufacturer.Name, form.Port));
    }

    [Fact]
    public void Should_KeepATypedPort_WhenTheBoardsChange()
    {
        var form = new AddBoardForm(_boards);
        form.Open();
        form.Port = "12345";

        Assert.Null(_boards.Add("vm201", SimulatedBoardsTests.FreePort(), null, null));
        form.Refresh();

        Assert.Equal("12345", form.Port);
    }
}
