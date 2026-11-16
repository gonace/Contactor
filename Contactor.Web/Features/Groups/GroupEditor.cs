using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// What the group's page edits: its steps in start order, each with its name, its wait, its run-on and its relays.
/// Relays and steps are added, removed and moved here; the page saves the result through <see cref="GroupsHandler"/>,
/// which keeps the rules (e.g. only while the group is off).
/// </summary>
public sealed class GroupEditor
{
    private readonly List<EditStep> _steps = [];
    private readonly List<EditRelay> _removed = [];

    // The group's relays as saved: only those, taken out, are offered again from here; the others are free relays,
    // offered (or not, once another group has one) by the page's list of free relays.
    private readonly HashSet<int> _saved = [];

    public GroupEditor(IReadOnlyList<GroupStepInfo> steps)
    {
        Reset(steps);
    }

    /// <summary>The steps in start order.</summary>
    public IReadOnlyList<EditStep> Steps => _steps;

    /// <summary>The group's own saved relays taken out and not saved yet: they can be added back.</summary>
    public IReadOnlyList<EditRelay> Removed => _removed;

    /// <summary>How many relays the group has, in all its steps.</summary>
    public int RelayCount => _steps.Sum(s => s.Relays.Count);

    /// <summary>The relays in start order: the steps' relays one after another.</summary>
    public IEnumerable<EditRelay> Relays => _steps.SelectMany(s => s.Relays);

    /// <summary>What to save.</summary>
    public IReadOnlyList<GroupStepDraft> Drafts =>
        [.. _steps.Select(s => new GroupStepDraft(s.Name, [.. s.Relays.Select(r => r.RelayId)], s.StartDelaySec, s.RunOnSec))];

    /// <summary>Starts again from the group as saved.</summary>
    public void Reset(IReadOnlyList<GroupStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps.Clear();
        _removed.Clear();
        _saved.Clear();
        _saved.UnionWith(steps.SelectMany(s => s.Relays).Select(r => r.RelayId));
        foreach (var step in steps)
        {
            _steps.Add(new EditStep(step.Name, step.StartDelaySec, step.RunOnSec, step.Relays.Select(r => new EditRelay(r.RelayId, r.Channel, r.Name))));
        }
    }

    /// <summary>
    /// Follows the group as saved elsewhere while this form keeps its own changes: only the relays the group now has
    /// are offered again from here once taken out, so one that another group took since isn't.
    /// </summary>
    public void FollowSaved(IReadOnlyList<GroupStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _saved.Clear();
        _saved.UnionWith(steps.SelectMany(s => s.Relays).Select(r => r.RelayId));
        _removed.RemoveAll(r => !_saved.Contains(r.RelayId));
    }

    /// <summary>A step named after a relay: its name, cut to what a step's name may be.</summary>
    internal static string StepName(string relayName)
    {
        return relayName.Length > RelayGroupStep.NameMaxLength ? relayName[..RelayGroupStep.NameMaxLength] : relayName;
    }

    /// <summary>Whether the steps hold the same relays, in the same order, as the group as saved.</summary>
    public bool HasSameRelays(IReadOnlyList<GroupStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Count == _steps.Count
            && steps.Zip(_steps).All(p => p.First.Relays.Select(r => r.RelayId).SequenceEqual(p.Second.Relays.Select(r => r.RelayId)));
    }

    /// <summary>Whether nothing differs from the group as saved: its relays, the steps' names and their times.</summary>
    public bool IsSameAs(IReadOnlyList<GroupStepInfo> steps)
    {
        return HasSameRelays(steps)
            && steps.Zip(_steps).All(p => p.First.Name == p.Second.Name && p.First.StartDelaySec == p.Second.StartDelaySec && p.First.RunOnSec == p.Second.RunOnSec);
    }

    /// <summary>Whether the relay is in one of the steps.</summary>
    public bool Contains(int relayId)
    {
        return Relays.Any(r => r.RelayId == relayId);
    }

    /// <summary>Adds a relay last in a step. One that is in the group already, or a step that isn't there, does nothing.</summary>
    public void AddToStep(int stepIndex, EditRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        if (stepIndex < 0 || stepIndex >= _steps.Count || Contains(relay.RelayId))
        {
            return;
        }

        _steps[stepIndex].Relays.Add(relay);
        _removed.RemoveAll(r => r.RelayId == relay.RelayId);
    }

    /// <summary>Adds a step last, with the relay as its only one, named after it.</summary>
    public void AddStep(EditRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        if (Contains(relay.RelayId))
        {
            return;
        }

        _steps.Add(new EditStep(StepName(relay.Name), RelayGroupStep.DefaultStartDelaySec, RelayGroupStep.DefaultRunOnSec, [relay]));
        _removed.RemoveAll(r => r.RelayId == relay.RelayId);
    }

    /// <summary>Takes a relay out; a step left without relays goes with it.</summary>
    public void Remove(int relayId)
    {
        foreach (var step in _steps)
        {
            var relay = step.Relays.FirstOrDefault(r => r.RelayId == relayId);
            if (relay is null)
            {
                continue;
            }

            step.Relays.Remove(relay);
            if (_saved.Contains(relay.RelayId))
            {
                _removed.Add(relay);
            }

            if (step.Relays.Count == 0)
            {
                _steps.Remove(step);
            }

            return;
        }
    }

    /// <summary>Moves a whole step up (-1) or down (+1); nothing past the ends.</summary>
    public void Move(int stepIndex, int by)
    {
        var to = stepIndex + by;
        if (stepIndex < 0 || stepIndex >= _steps.Count || to < 0 || to >= _steps.Count)
        {
            return;
        }

        (_steps[stepIndex], _steps[to]) = (_steps[to], _steps[stepIndex]);
    }
}

/// <summary>A step as the group's page edits it.</summary>
public sealed class EditStep
{
    public EditStep(string name, int startDelaySec, int runOnSec, IEnumerable<EditRelay> relays)
    {
        Name = name;
        StartDelaySec = startDelaySec;
        RunOnSec = runOnSec;
        Relays = [.. relays];
    }

    public string Name { get; set; }

    /// <summary>Its name, or while that is empty its first relay's: what it is saved as, e.g. for a screen reader.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? GroupEditor.StepName(Relays.FirstOrDefault()?.Name ?? "") : Name.Trim();

    /// <summary>The wait before it starts, once the step above is on. Unused for the first.</summary>
    public int StartDelaySec { get; set; }

    /// <summary>How long it keeps running once the step below is off. Unused for the last.</summary>
    public int RunOnSec { get; set; }

    public List<EditRelay> Relays { get; }
}

/// <summary>A relay of a step on the group's page: its channel is the number shown and what the board's state goes by.</summary>
public sealed record EditRelay(int RelayId, int Channel, string Name);
