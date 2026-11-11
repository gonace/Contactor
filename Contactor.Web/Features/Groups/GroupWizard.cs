using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// What "Ny grupp" holds between its four steps. The relays' order is one list: the order they were picked in, changed
/// by the arrows in step 3, so step 2's #1, #2 … and step 3 always show the same start order.
/// </summary>
public sealed class GroupWizard
{
    public const int StepCount = 4;

    private readonly List<WizardMember> _members = [];
    private readonly Dictionary<int, WizardMember> _unpicked = [];

    public int Step { get; private set; } = 1;

    public string Name { get; set; } = "";

    public string Icon { get; set; } = GroupIcons.Fan;

    /// <summary>The picked relays in start order.</summary>
    public IReadOnlyList<WizardMember> Members => _members;

    public bool CanGoOn => Step switch
    {
        1 => !string.IsNullOrWhiteSpace(Name),
        2 => _members.Count >= RelayGroup.MinMembers,
        3 => _members.All(m => !string.IsNullOrWhiteSpace(m.RoleName)),
        _ => false,
    };

    public GroupDraft Draft => new(
        Name,
        Icon,
        RelayGroup.DefaultMaxOnMinutes,
        [.. _members.Select(m => new GroupMemberDraft(m.RelayId, m.RoleName.Trim(), m.StartDelaySec, m.RunOnSec))]);

    /// <summary>1 for the first in the start order, or 0 when not picked.</summary>
    public int OrderOf(int relayId)
    {
        return _members.FindIndex(m => m.RelayId == relayId) + 1;
    }

    /// <summary>Picks a relay last in the order, or unpicks it. A relay picked again keeps its name and waits.</summary>
    public void Toggle(int relayId, string relayName)
    {
        ArgumentNullException.ThrowIfNull(relayName);
        var index = _members.FindIndex(m => m.RelayId == relayId);
        if (index >= 0)
        {
            _unpicked[relayId] = _members[index];
            _members.RemoveAt(index);
            return;
        }

        if (_unpicked.Remove(relayId, out var earlier))
        {
            _members.Add(earlier);
            return;
        }

        var role = relayName.Length > RelayGroupMember.RoleNameMaxLength ? relayName[..RelayGroupMember.RoleNameMaxLength] : relayName;
        _members.Add(new WizardMember(relayId, role));
    }

    public void Move(int index, int step)
    {
        var to = index + step;
        if (index < 0 || index >= _members.Count || to < 0 || to >= _members.Count)
        {
            return;
        }

        (_members[index], _members[to]) = (_members[to], _members[index]);
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

/// <summary>A relay as "Ny grupp" sets it up.</summary>
public sealed class WizardMember
{
    public WizardMember(int relayId, string roleName)
    {
        RelayId = relayId;
        RoleName = roleName;
    }

    public int RelayId { get; }

    public string RoleName { get; set; }

    public int StartDelaySec { get; set; } = RelayGroupMember.DefaultStartDelaySec;

    public int RunOnSec { get; set; } = RelayGroupMember.DefaultRunOnSec;
}
