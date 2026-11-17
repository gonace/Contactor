using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// What "New group" holds between its four steps. The relays are in steps, edited as the group's page edits them
/// (<see cref="GroupEditor"/>): a relay picked in step 2 starts a step of its own, last; in step 3 a relay can join a
/// step or the step above, be added or removed, and a whole step moves. Step 2's #1, #2 … are the relays' places in
/// that start order.
/// </summary>
public sealed class GroupWizard
{
    public const int StepCount = 4;

    private readonly GroupEditor _editor = new([]);

    // A relay unpicked while alone in its step: that step's name and waits, for when it is picked again.
    private readonly Dictionary<int, (string Name, int StartDelaySec, int RunOnSec)> _unpicked = [];
    private int _boardId;

    public int Step { get; private set; } = 1;

    public string Name { get; set; } = "";

    public string Icon { get; set; } = GroupIcons.Fan;

    /// <summary>The steps in start order, each with its relays.</summary>
    public GroupEditor Editor => _editor;

    /// <summary>How many relays are picked, in all the steps.</summary>
    public int RelayCount => _editor.RelayCount;

    public bool CanGoOn => Step switch
    {
        1 => !string.IsNullOrWhiteSpace(Name),
        2 => _editor.RelayCount >= RelayGroup.MinMembers,
        // A step's name may be empty, as on the group's page: it is then saved as its first relay's.
        3 => _editor.RelayCount >= RelayGroup.MinMembers,
        _ => false,
    };

    /// <summary>The group to create: its steps as the group's page saves them.</summary>
    public GroupDraft Draft => new(Name, Icon, RelayGroup.DefaultMaxOnMinutes, _editor.Drafts);

    /// <summary>
    /// The steps in start order for the plan in words: each by its first relay, with the step's name (its first relay's
    /// while empty, as it is saved) and times.
    /// </summary>
    public IReadOnlyList<GroupMemberDraft> StepDrafts =>
        [.. _editor.Steps.Select(s => new GroupMemberDraft(s.Relays[0].RelayId, s.Label, s.StartDelaySec, s.RunOnSec))];

    /// <summary>The relay's place in the start order, from 1 (the steps' relays one after another), or 0 when not picked.</summary>
    public int OrderOf(int relayId)
    {
        return _editor.Relays.Select(r => r.RelayId).ToList().IndexOf(relayId) + 1;
    }

    /// <summary>
    /// The board of the picked relays: the first picked relay's, or null while none is picked. A group's relays are all
    /// on one board, so only that board's relays can be picked next.
    /// </summary>
    public int? BoardId => _editor.RelayCount > 0 ? _boardId : null;

    /// <summary>Whether a relay on this board can be picked next: none is picked yet, or the picked ones are on it.</summary>
    public bool FitsBoard(int boardId)
    {
        return BoardId is not { } picked || picked == boardId;
    }

    /// <summary>
    /// Picks a relay on this board in a step of its own, last, or unpicks it. A relay picked again that was alone in its
    /// step gets that step's name and waits back. One on another board than the picked relays' isn't picked.
    /// </summary>
    public void Toggle(int relayId, int channel, string relayName, int boardId)
    {
        ArgumentNullException.ThrowIfNull(relayName);
        if (_editor.Contains(relayId))
        {
            Remove(relayId);
            return;
        }

        Add(GroupEditor.NewStep, new EditRelay(relayId, channel, relayName), boardId);
    }

    /// <summary>
    /// Adds a relay on this board to the step at <paramref name="stepIndex"/>, or with <see cref="GroupEditor.NewStep"/>
    /// in a step of its own, last. One already picked, on another board, or a step that isn't there, does nothing.
    /// </summary>
    public void Add(int stepIndex, EditRelay relay, int boardId)
    {
        ArgumentNullException.ThrowIfNull(relay);
        if (_editor.Contains(relay.RelayId) || !FitsBoard(boardId))
        {
            return;
        }

        if (stepIndex != GroupEditor.NewStep && (stepIndex < 0 || stepIndex >= _editor.Steps.Count))
        {
            return;
        }

        if (_editor.RelayCount == 0)
        {
            _boardId = boardId;
        }

        if (stepIndex != GroupEditor.NewStep)
        {
            _editor.AddToStep(stepIndex, relay);
            _unpicked.Remove(relay.RelayId);
            return;
        }

        _editor.AddStep(relay);
        if (_unpicked.Remove(relay.RelayId, out var earlier))
        {
            var step = _editor.Steps[^1];
            step.Name = earlier.Name;
            step.StartDelaySec = earlier.StartDelaySec;
            step.RunOnSec = earlier.RunOnSec;
        }
    }

    /// <summary>Unpicks a relay; a step left without relays goes with it.</summary>
    public void Remove(int relayId)
    {
        if (_editor.Steps.FirstOrDefault(s => s.Relays.Any(r => r.RelayId == relayId)) is not { } step)
        {
            return;
        }

        if (step.Relays.Count == 1)
        {
            _unpicked[relayId] = (step.Name, step.StartDelaySec, step.RunOnSec);
        }

        _editor.Remove(relayId);
    }

    /// <summary>Moves a relay into the step above, as the group's page does (<see cref="GroupEditor.JoinStepAbove"/>).</summary>
    public void JoinStepAbove(int relayId)
    {
        _editor.JoinStepAbove(relayId);
    }

    /// <summary>Moves a whole step up (-1) or down (+1); nothing past the ends.</summary>
    public void Move(int stepIndex, int by)
    {
        _editor.Move(stepIndex, by);
    }

    /// <summary>Goes on from <paramref name="fromStep"/>; a second tap that arrives after the page moved on does nothing.</summary>
    public bool GoOn(int fromStep)
    {
        if (fromStep != Step || !CanGoOn)
        {
            return false;
        }

        Step++;
        return true;
    }

    /// <summary>Goes back from <paramref name="fromStep"/>; never before step 1, and a second tap does nothing.</summary>
    public bool Back(int fromStep)
    {
        if (fromStep != Step || Step <= 1)
        {
            return false;
        }

        Step--;
        return true;
    }
}
