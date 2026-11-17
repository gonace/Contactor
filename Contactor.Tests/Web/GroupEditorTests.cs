using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// What the group's page edits: relays added to a step or as a new step, removed, moved into the step above, and steps
/// moved.
/// </summary>
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

    // Heat (step 2 of 3) joins the fan: its step goes, and with it its wait and run-on; the fan's step keeps its name
    // and times, and the lamp's step is the second now, with its own.
    [Fact]
    public void Should_JoinTheStepAbove_AndDropTheEmptyStepWithItsTimes()
    {
        IReadOnlyList<GroupStepInfo> fanHeatLamp =
        [
            new("Fan", 3, 300, [new(11, 1, "Fan 1")]),
            new("Heat", 10, 60, [new(12, 2, "Heater 1")]),
            new("Lamp", 5, 0, [new(14, 4, "Lamp")]),
        ];
        var editor = new GroupEditor(fanHeatLamp);

        editor.JoinStepAbove(12);

        Assert.Equal(["Fan:11,12:3:300", "Lamp:14:5:0"], editor.Drafts.Select(Describe));
        Assert.Empty(editor.Removed);
    }

    // A step with another relay left stays as it was: the relay goes last in the step above.
    [Fact]
    public void Should_JoinTheStepAbove_KeepingAStepThatStillHasRelays()
    {
        var editor = new GroupEditor([FanHeater[0], new("Heat", 10, 0, [new(12, 2, "Heater 1"), new(13, 3, "Heater 2")])]);
        editor.AddToStep(0, new EditRelay(15, 5, "Fan 3"));

        editor.JoinStepAbove(12);

        Assert.Equal(["Fan:11,15,12:3:300", "Heat:13:10:0"], editor.Drafts.Select(Describe));
    }

    // Two relays in two steps become one step of two, the first's.
    [Fact]
    public void Should_JoinTwoStepsIntoOne()
    {
        var editor = new GroupEditor(FanHeater);

        editor.JoinStepAbove(12);

        Assert.Equal(["Fan:11,12:3:300"], editor.Drafts.Select(Describe));
        Assert.Equal(2, editor.RelayCount);
    }

    [Fact]
    public void Should_JoinNothing_FromTheFirstStep_OrForARelayNotInTheGroup()
    {
        var editor = new GroupEditor(FanHeater);

        editor.JoinStepAbove(11);
        editor.JoinStepAbove(99);

        Assert.True(editor.IsSameAs(FanHeater));
    }

    // A free relay added and taken out again isn't kept: the page offers it from the free relays, once, and not at
    // all once another group has taken it.
    [Fact]
    public void Should_OfferAgain_OnlyTheGroupsOwnSavedRelays()
    {
        var editor = new GroupEditor(FanHeater);
        editor.AddToStep(0, Fan2);

        editor.Remove(13);
        editor.Remove(12);

        Assert.Equal([12], editor.Removed.Select(r => r.RelayId));
        Assert.True(editor.HasSameRelays([FanHeater[0]]));
    }

    [Fact]
    public void Should_NameAStepWithoutAName_AfterItsFirstRelay()
    {
        var editor = new GroupEditor(FanHeater);

        editor.Steps[0].Name = "  ";

        Assert.Equal(("Fan 1", "Heat"), (editor.Steps[0].Label, editor.Steps[1].Label));
    }

    // Only the screen reader's label: a step is saved with its relay's name cut to what a step's name may be.
    [Fact]
    public void Should_CutALongRelayName_InAStepsLabel_AsItIsSaved()
    {
        var longName = new string('x', RelayGroupStep.NameMaxLength + 5);
        var editor = new GroupEditor([new("", 3, 0, [new(11, 1, longName)])]);

        Assert.Equal(longName[..RelayGroupStep.NameMaxLength], editor.Steps[0].Label);
    }

    // Someone else changed the group while this form has changes of its own: a relay the group no longer has isn't
    // offered again from here, since another group may have taken it.
    [Fact]
    public void Should_OfferAgain_OnlyRelaysTheGroupStillHas_AfterAChangeElsewhere()
    {
        var editor = new GroupEditor(FanHeater);
        editor.Remove(12);

        editor.AddToStep(0, Fan2);

        editor.FollowSaved([FanHeater[0], new("Fan 2", 3, 0, [new(13, 3, "Fan 2")])]);
        Assert.Empty(editor.Removed);

        editor.Remove(13);
        Assert.Equal([13], editor.Removed.Select(r => r.RelayId));
    }

    [Fact]
    public void Should_KeepOfferingARelayTheGroupStillHas_AfterAChangeElsewhere()
    {
        var editor = new GroupEditor(FanHeater);
        editor.Remove(12);

        editor.FollowSaved([FanHeater[0], FanHeater[1], new("Lamp", 3, 0, [new(14, 4, "Lamp")])]);

        Assert.Equal([12], editor.Removed.Select(r => r.RelayId));
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

    private static string Describe(GroupStepDraft step)
    {
        return $"{step.Name}:{string.Join(',', step.RelayIds)}:{step.StartDelaySec}:{step.RunOnSec}";
    }
}
