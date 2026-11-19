using System.Globalization;

namespace Contactor.Simulator;

/// <summary>
/// The page's "Add a board" panel: open or not, the manufacturer and model chosen, what is typed and the error shown.
/// It starts afresh each time it opens, with the catalogue's first model and the next free port for it.
/// </summary>
public sealed class AddBoardForm
{
    private readonly SimulatedBoards _boards;
    private string _offeredPort = "";

    public AddBoardForm(SimulatedBoards boards)
    {
        ArgumentNullException.ThrowIfNull(boards);
        _boards = boards;
        Model = SimulatorCatalogue.All[0];
        Manufacturer = ManufacturerOf(Model);
    }

    public bool IsOpen { get; private set; }

    public SimulatorManufacturer Manufacturer { get; private set; }

    /// <summary>The model chosen: always one of <see cref="Manufacturer"/>'s.</summary>
    public SimulatorModel Model { get; private set; }

    public string Port { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>As typed; never shown anywhere but in its own field.</summary>
    public string Password { get; set; } = "";

    public string? Error { get; private set; }

    /// <summary>"Add Velleman VM201 on port 9761", or without the port while it isn't one.</summary>
    public string AddText => ParsePort() is { } port
        ? string.Create(CultureInfo.InvariantCulture, $"Add {Model.Model.Name} on port {port}")
        : $"Add {Model.Model.Name}";

    /// <summary>
    /// Which board has the port typed, e.g. "9760 is taken by VM201.", or else the model's own port when another board
    /// has it, which is why another is offered; null when neither is taken.
    /// </summary>
    public string? PortNote
    {
        get
        {
            if (ParsePort() is { } port && TakenBy(port) is { } board)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{port} is taken by {board.ShortName}.");
            }

            return ParsePort() != Model.Port && TakenBy(Model.Port) is { } own
                ? string.Create(CultureInfo.InvariantCulture, $"{Model.Port} is taken by {own.ShortName}.")
                : null;
        }
    }

    /// <summary>Opens the panel afresh: the catalogue's first model, its next free port, no login.</summary>
    public void Open()
    {
        IsOpen = true;
        Model = SimulatorCatalogue.All[0];
        Manufacturer = ManufacturerOf(Model);
        StartModel();
    }

    /// <summary>Closes the panel; nothing typed is kept.</summary>
    public void Cancel()
    {
        IsOpen = false;
        Username = "";
        Password = "";
        Error = null;
    }

    /// <summary>Chooses a manufacturer and its first model; nothing for a name the catalogue doesn't have, or the one chosen.</summary>
    public void ChooseManufacturer(string? name)
    {
        if (name == Manufacturer.Name || SimulatorCatalogue.Manufacturers.FirstOrDefault(m => m.Name == name) is not { } manufacturer)
        {
            return;
        }

        Manufacturer = manufacturer;
        Model = manufacturer.Models[0];
        StartModel();
    }

    /// <summary>
    /// Chooses one of the manufacturer's models, with its next free port and no login, as its login fields may be others;
    /// nothing for an id it doesn't have, or the one chosen.
    /// </summary>
    public void ChooseModel(string? id)
    {
        if (id == Model.Model.Id || Manufacturer.Models.FirstOrDefault(m => m.Model.Id == id) is not { } model)
        {
            return;
        }

        Model = model;
        StartModel();
    }

    /// <summary>
    /// After the boards changed: a port the panel offered, not one typed in, is offered afresh, as another board may
    /// have it now.
    /// </summary>
    public void Refresh()
    {
        if (Port == _offeredPort)
        {
            OfferPort();
        }
    }

    /// <summary>
    /// Starts the board as typed. Returns its port once it runs, and closes the panel; otherwise null, with the reason
    /// in <see cref="Error"/>.
    /// </summary>
    public int? Add()
    {
        if (ParsePort() is not { } port)
        {
            Error = "The port is a number from 1 to 65535.";
            return null;
        }

        if (TakenBy(port) is { } board)
        {
            Error = string.Create(CultureInfo.InvariantCulture, $"Port {port} is taken by {board.ShortName}.");
            return null;
        }

        Error = _boards.Add(Model.Model.Id, port, Username.Trim(), Password);
        if (Error is not null)
        {
            return null;
        }

        Cancel();
        return port;
    }

    private static SimulatorManufacturer ManufacturerOf(SimulatorModel model)
    {
        return SimulatorCatalogue.Manufacturers.First(m => m.Models.Contains(model));
    }

    // Another model: its own port, no login, and an error about the one before no longer applies.
    private void StartModel()
    {
        Username = "";
        Password = "";
        Error = null;
        OfferPort();
    }

    // The model's suggested port, or the first free one after it; empty when none is free.
    private void OfferPort()
    {
        var taken = _boards.Boards.Select(b => b.Board.Port).ToHashSet();
        _offeredPort = SimulatorCatalogue.FreePort(Model.Model.Id, taken)?.ToString(CultureInfo.InvariantCulture) ?? "";
        Port = _offeredPort;
    }

    private int? ParsePort()
    {
        return int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;
    }

    private RunningBoard? TakenBy(int port)
    {
        return _boards.Boards.FirstOrDefault(b => b.Board.Port == port);
    }
}
