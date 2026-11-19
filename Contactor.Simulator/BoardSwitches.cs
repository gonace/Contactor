using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Velleman;
using Contactor.Boards.Waveshare;

namespace Contactor.Simulator;

/// <summary>
/// The page's switches for what a simulated board can do wrong, for the boards that have each: unreachable, hung, a relay
/// refused, and each model's own cases (another Devantech module, the VM204's new key, the VM208 busy, the DAEnetIP2's
/// lost requests, the Waveshare's refused relay modes). Each changes the board at once and says so in the traffic. What a
/// board doesn't have is null or false, and setting it changes nothing. The Shelly's are in <see cref="ShellyControls"/>.
/// </summary>
public sealed class BoardSwitches
{
    private readonly ISimulatedBoard _board;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();

    public BoardSwitches(ISimulatedBoard board, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(log);
        _board = board;
        _log = log;
    }

    private int RelayCount => _board.Model.Capabilities.RelayCount;

    /// <summary>Whether new connections are closed at once; null for a board without the switch (a UDP board).</summary>
    public bool? RejectConnections => (_board as ISimulatedBoardRejectConnections)?.RejectConnections;

    /// <summary>Whether the board reads requests and answers none; null for a board without the switch.</summary>
    public bool? NoAnswer => (_board as ISimulatedBoardNoAnswer)?.NoAnswer;

    /// <summary>The relays whose switch command the board refuses; null for a board without the switch.</summary>
    public ChannelMask? FailRelays => _board is ISimulatedBoardFailRelays fail ? new ChannelMask(fail.FailRelays) : null;

    /// <summary>The relays whose pulse is running; empty for a board without a pulse.</summary>
    public ChannelMask Pulsing => (_board as ISimulatedBoardPulses)?.Pulsing ?? ChannelMask.None;

    /// <summary>The Devantech models whose module id the board can answer, so the app sees another model; empty for any other board.</summary>
    public IReadOnlyList<BoardModel> Modules => _board is DevantechSimulator ? DevantechModels.All : [];

    /// <summary>The model whose module id a Devantech board answers, or null (another board, or an id of no model).</summary>
    public BoardModel? Module => _board is DevantechSimulator devantech ? DevantechModels.ModelFor(devantech.ModuleId) : null;

    /// <summary>Whether the board has a key that can be made anew, as the VM204's web page does.</summary>
    public bool HasKey => _board is Vm204Simulator;

    /// <summary>How many of the next statuses come busy; null for a board other than the VM208.</summary>
    public int? BusyNext => (_board as Vm208Simulator)?.BusyNext;

    /// <summary>How many of the next requests are lost; null for a board other than the DAEnetIP2.</summary>
    public int? DropRequests => (_board as DaenetIp2Simulator)?.DropRequests;

    /// <summary>The relays whose set is never answered nor carried out; null for a board other than the DAEnetIP2.</summary>
    public ChannelMask? SilentRelays => _board is DaenetIp2Simulator daenet ? new ChannelMask(daenet.SilentRelays) : null;

    /// <summary>Whether a get of an analog input goes unanswered; null for a board other than the DAEnetIP2.</summary>
    public bool? NoAnalogAnswer => (_board as DaenetIp2Simulator)?.NoAnalogAnswer;

    /// <summary>Whether reads of the relay modes are refused; null for a board without relay modes.</summary>
    public bool? RefuseModeReads => HasModes(out var waveshare) ? waveshare.RefuseModeReads : null;

    /// <summary>Whether writes of the relay modes are refused; null for a board without relay modes.</summary>
    public bool? RefuseModeWrites => HasModes(out var waveshare) ? waveshare.RefuseModeWrites : null;

    public void SetRejectConnections(bool reject)
    {
        if (_board is not ISimulatedBoardRejectConnections board || !Change(() => board.RejectConnections, v => board.RejectConnections = v, reject))
        {
            return;
        }

        _log(reject ? "New connections refused here (unreachable)" : "New connections taken again here");
    }

    public void SetNoAnswer(bool noAnswer)
    {
        if (_board is not ISimulatedBoardNoAnswer board || !Change(() => board.NoAnswer, v => board.NoAnswer = v, noAnswer))
        {
            return;
        }

        _log(noAnswer ? "Stopped answering here (hung)" : "Answering again here");
    }

    /// <summary>Makes the board refuse, or carry out again, one relay's switch command; the others stay as they are.</summary>
    public void SetFailRelay(int relay, bool fail)
    {
        if (_board is not ISimulatedBoardFailRelays board || relay < 1 || relay > RelayCount)
        {
            return;
        }

        var bit = 1UL << (relay - 1);
        if (!Change(() => (board.FailRelays & bit) != 0, v => board.FailRelays = v ? board.FailRelays | bit : board.FailRelays & ~bit, fail))
        {
            return;
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s command {(fail ? "refused from now on" : "carried out again")} here"));
    }

    /// <summary>Makes a Devantech board answer another model's module id; nothing for an id it doesn't offer.</summary>
    public void SetModule(string? id)
    {
        if (_board is not DevantechSimulator devantech || Modules.FirstOrDefault(m => m.Id == id) is not { } model)
        {
            return;
        }

        var moduleId = DevantechModels.Layout(model).ModuleId;
        if (!Change(() => devantech.ModuleId, v => devantech.ModuleId = v, moduleId))
        {
            return;
        }

        _log(model == _board.Model ? $"Answering as an {model.ModelName} again here" : $"Answering as an {model.ModelName} here (another model)");
    }

    /// <summary>Makes the VM204's key anew, as its web page does: the app's key stops working.</summary>
    public void RegenerateKey()
    {
        if (_board is not Vm204Simulator vm204)
        {
            return;
        }

        vm204.RegenerateKey();
        _log("A new key made here: the app's key no longer works");
    }

    /// <summary>Makes the VM208's next status come busy, one more each press.</summary>
    public void BusyOnce()
    {
        if (_board is not Vm208Simulator vm208)
        {
            return;
        }

        lock (_lock)
        {
            vm208.BusyNext = Math.Max(vm208.BusyNext, 0) + 1;
        }

        _log("The next status comes busy here");
    }

    /// <summary>Makes the DAEnetIP2 lose its next request, one more each press.</summary>
    public void DropOnce()
    {
        if (_board is not DaenetIp2Simulator daenet)
        {
            return;
        }

        lock (_lock)
        {
            daenet.DropRequests++;
        }

        _log("The next request is lost here");
    }

    /// <summary>Makes the DAEnetIP2 leave one relay's set unanswered and not carried out, or answer it again.</summary>
    public void SetSilentRelay(int relay, bool silent)
    {
        if (_board is not DaenetIp2Simulator daenet || relay < 1 || relay > RelayCount)
        {
            return;
        }

        var bit = 1UL << (relay - 1);
        if (!Change(() => (daenet.SilentRelays & bit) != 0, v => daenet.SilentRelays = v ? daenet.SilentRelays | bit : daenet.SilentRelays & ~bit, silent))
        {
            return;
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s set {(silent ? "lost from now on" : "answered again")} here"));
    }

    public void SetNoAnalogAnswer(bool noAnswer)
    {
        if (_board is not DaenetIp2Simulator daenet || !Change(() => daenet.NoAnalogAnswer, v => daenet.NoAnalogAnswer = v, noAnswer))
        {
            return;
        }

        _log(noAnswer ? "Analog inputs no longer answered here" : "Analog inputs answered again here");
    }

    public void SetRefuseModeReads(bool refuse)
    {
        if (!HasModes(out var waveshare) || !Change(() => waveshare.RefuseModeReads, v => waveshare.RefuseModeReads = v, refuse))
        {
            return;
        }

        _log(refuse ? "Reads of the relay modes refused here" : "Reads of the relay modes answered again here");
    }

    public void SetRefuseModeWrites(bool refuse)
    {
        if (!HasModes(out var waveshare) || !Change(() => waveshare.RefuseModeWrites, v => waveshare.RefuseModeWrites = v, refuse))
        {
            return;
        }

        _log(refuse ? "Writes of the relay modes refused here" : "Writes of the relay modes taken again here");
    }

    // Only the Waveshare models with relay modes, the (B) and (C), have them to refuse.
    private bool HasModes(out WaveshareSimulator waveshare)
    {
        waveshare = (_board as WaveshareSimulator)!;
        return waveshare is not null && WaveshareModels.HasRelayModes(_board.Model);
    }

    // Sets the value unless it already is that; true when it changed. One page at a time changes a board.
    private bool Change<T>(Func<T> get, Action<T> set, T value)
    {
        lock (_lock)
        {
            if (EqualityComparer<T>.Default.Equals(get(), value))
            {
                return false;
            }

            set(value);
            return true;
        }
    }
}
