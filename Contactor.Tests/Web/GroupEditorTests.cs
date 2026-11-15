using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>What the group's page edits: relays added to a step or as a new step, removed, and steps moved.</summary>
public sealed class GroupEditorTests
{
    private static readonly IReadOnlyList<GroupStepInfo> FanHeater =
    [
        new("Fan", 3, 300, [new(11, 1, "Fan 1")]),
        new("Heat", 3, 0, [new(12, 2, "Heater 1")]),
    ];

    private static readonly EditRelay Fan2 = new(13, 3, "Fan 2");

    [Fact]
    public void Should_AddARelayToAStep_AndAStepNamedAfterItsRelay()
    {
        var editor = new GroupEditor(FanHeater);

        editor.AddToStep(0, Fan2);
        editor.AddStep(new EditRelay(14, 4, "Lamp"));

        Assert.Equal(["Fan:11,13", "Heat:12", "Lamp:14"], editor.Drafts.Select(d => $"{d.Name}:{string.Join(',', d.RelayIds)}"));
        Assert.Equal((RelayGroupStep.DefaultStartDelaySec, RelayGroupStep.DefaultRunOnSec), (editor.Steps[2].StartDelaySec, editor.Steps[2].RunOnSec));
        Assert.Equal(4, editor.RelayCount);
        Assert.False(editor.HasSameRelays(FanHeater));
    }

    [Fact]
    public void Should_AddARelayOnlyOnce_AndOnlyToAStepThatIsThere()
    {
        var editor = new GroupEditor(FanHeater);

        editor.AddToStep(1, new EditRelay(11, 1, "Fan 1"));
        editor.AddStep(new EditRelay(12, 2, "Heater 1"));
        editor.AddToStep(2, Fan2);
        editor.AddToStep(-1, Fan2);

        Assert.True(editor.HasSameRelays(FanHeater));
    }

    [Fact]
    public void Should_RemoveAStep_WithItsLastRelay_AndOfferTheRelayAgain()
    {
        var editor = new GroupEditor(FanHeater);
        editor.AddToStep(0, Fan2);

        editor.Remove(12);

        Assert.Equal(["Fan"], editor.Steps.Select(s => s.Name));
        Assert.Equal([12], editor.Removed.Select(r => r.RelayId));

        editor.AddStep(editor.Removed[0]);
        Assert.Empty(editor.Removed);
        Assert.Equal(["Fan", "Heater 1"], editor.Steps.Select(s => s.Name));
    }

    [Fact]
    public void Should_MoveAWholeStep_ButNotPastTheEnds()
    {
        var editor = new GroupEditor(FanHeater);
        editor.AddToStep(0, Fan2);

        editor.Move(0, 1);
        editor.Move(1, 1);
        editor.Move(0, -1);

        Assert.Equal([12, 11, 13], editor.Relays.Select(r => r.RelayId));
    }

    [Fact]
    public void Should_TellANameOrTimeChange_FromAChangeOfRelays()
    {
        var editor = new GroupEditor(FanHeater);
        Assert.True(editor.IsSameAs(FanHeater));

        editor.Steps[0].Name = "Fans";
        editor.Steps[1].StartDelaySec = 10;

        Assert.False(editor.IsSameAs(FanHeater));
        Assert.True(editor.HasSameRelays(FanHeater));
        Assert.Equal([("Fans", 3, 300), ("Heat", 10, 0)], editor.Drafts.Select(d => (d.Name!, d.StartDelaySec, d.RunOnSec)));

        editor.Reset(FanHeater);
        Assert.True(editor.IsSameAs(FanHeater));
    }
}
