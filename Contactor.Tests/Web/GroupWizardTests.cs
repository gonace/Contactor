using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>"New group" between its steps: one start order, and taps that arrive after the page moved on.</summary>
public sealed class GroupWizardTests
{
    private const int Board = 1;

    [Fact]
    public void Should_KeepStepTwosNumbers_InTheOrderStepThreeChose()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(3, 3, "Relay 3", Board);
        wizard.Toggle(1, 1, "Relay 1", Board);
        Assert.True(wizard.GoOn(2));

        wizard.Move(1, -1);
        Assert.True(wizard.Back(3));

        Assert.Equal((1, 2), (wizard.OrderOf(1), wizard.OrderOf(3)));
        Assert.Equal([1, 3], wizard.Draft.RelayIds);
    }

    [Fact]
    public void Should_PutARelayPickedAgainLast_KeepingItsNameAndWaits()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(3, 3, "Relay 3", Board);
        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(5, 5, "Relay 5", Board);
        wizard.Editor.Steps[0].Name = "Fan";
        wizard.Editor.Steps[0].RunOnSec = 300;

        wizard.Toggle(3, 3, "Relay 3", Board);
        wizard.Toggle(3, 3, "Relay 3", Board);

        Assert.Equal([1, 5, 3], RelayIds(wizard));
        Assert.Equal(3, wizard.OrderOf(3));
        Assert.Equal(("Fan", 300), (wizard.Editor.Steps[2].Name, wizard.Editor.Steps[2].RunOnSec));
    }

    [Fact]
    public void Should_IgnoreASecondTap_ThatArrivesAfterThePageMovedOn()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(2, 2, "Relay 2", Board);

        Assert.True(wizard.Back(2));
        Assert.False(wizard.Back(2));
        Assert.False(wizard.Back(1));
        Assert.Equal(1, wizard.Step);

        Assert.True(wizard.GoOn(1));
        Assert.True(wizard.GoOn(2));
        Assert.False(wizard.GoOn(2));
        Assert.Equal(3, wizard.Step);
    }

    [Fact]
    public void Should_GoOnOnlyWithWhatEachStepNeeds()
    {
        var wizard = new GroupWizard();
        Assert.False(wizard.GoOn(1));
        wizard.Name = "   ";
        Assert.False(wizard.GoOn(1));
        wizard.Name = "Fan heater";
        Assert.True(wizard.GoOn(1));

        wizard.Toggle(1, 1, "Relay 1", Board);
        Assert.False(wizard.GoOn(2));
        wizard.Toggle(2, 2, "Relay 2", Board);
        Assert.True(wizard.GoOn(2));

        wizard.Editor.Steps[0].Name = " Fan ";
        wizard.Editor.Steps[1].Name = " ";
        Assert.True(wizard.GoOn(3));

        Assert.False(wizard.GoOn(4));
        Assert.Equal(GroupWizard.StepCount, wizard.Step);
        Assert.Equal(["Fan", "Relay 2"], wizard.StepDrafts.Select(s => s.RoleName));
    }

    [Fact]
    public void Should_IgnoreMovesOutsideTheList()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(2, 2, "Relay 2", Board);

        wizard.Move(0, -1);
        wizard.Move(1, 1);
        wizard.Move(5, -1);
        wizard.Move(-1, 1);

        Assert.Equal([1, 2], RelayIds(wizard));
    }

    [Fact]
    public void Should_CutALongRelayNameToARoleName()
    {
        var wizard = AtStepTwo();

        wizard.Toggle(1, 1, new string('Å', 60), Board);

        Assert.Equal(RelayGroupStep.NameMaxLength, wizard.Editor.Steps[0].Name.Length);
    }

    // Two relays in a step, two in the next: a relay joins the step above (taken out, then added to it), and one
    // more is added to a step. The draft is the steps, as the group's page saves them.
    [Fact]
    public void Should_PutSeveralRelaysInAStep()
    {
        var wizard = AtStepTwo();
        foreach (var id in new[] { 1, 2, 3, 4 })
        {
            wizard.Toggle(id, id, $"Relay {id}", Board);
        }

        wizard.Editor.Steps[0].Name = "Fans";
        wizard.Remove(2);
        wizard.Add(0, new EditRelay(2, 2, "Relay 2"), Board);
        wizard.Editor.Steps[1].Name = "Heaters";
        wizard.Remove(4);
        wizard.Add(1, new EditRelay(4, 4, "Relay 4"), Board);

        Assert.Equal([[1, 2], [3, 4]], wizard.Draft.Steps.Select(s => s.RelayIds.ToArray()));
        Assert.Equal(["Fans", "Heaters"], wizard.Draft.Steps.Select(s => s.Name));
        Assert.Equal((3, 4), (wizard.OrderOf(3), wizard.OrderOf(4)));
        Assert.Equal(["Fans", "Heaters"], wizard.StepDrafts.Select(s => s.RoleName));
    }

    // One already picked, on another board, or for a step that isn't there, isn't added.
    [Fact]
    public void Should_AddOnlyAFreshRelay_OnTheGroupsBoard_ToAStepThatIsThere()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(2, 2, "Relay 2", Board);

        wizard.Add(0, new EditRelay(2, 2, "Relay 2"), Board);
        wizard.Add(0, new EditRelay(9, 1, "Barn 1"), Board + 1);
        wizard.Add(2, new EditRelay(3, 3, "Relay 3"), Board);
        wizard.Add(-2, new EditRelay(3, 3, "Relay 3"), Board);

        Assert.Equal([[1], [2]], wizard.Draft.Steps.Select(s => s.RelayIds.ToArray()));

        wizard.Add(GroupEditor.NewStep, new EditRelay(3, 3, "Relay 3"), Board);
        Assert.Equal([[1], [2], [3]], wizard.Draft.Steps.Select(s => s.RelayIds.ToArray()));
    }

    // A step's last relay unpicked takes the step with it; the board is free again once nothing is picked.
    [Fact]
    public void Should_DropAnEmptyStep_AndFreeTheBoard_WhenNothingIsPicked()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(2, 2, "Relay 2", Board);
        wizard.Toggle(3, 3, "Relay 3", Board);
        wizard.Remove(2);
        wizard.Add(0, new EditRelay(2, 2, "Relay 2"), Board);

        wizard.Toggle(1, 1, "Relay 1", Board);
        wizard.Toggle(2, 2, "Relay 2", Board);
        Assert.Equal([[3]], wizard.Draft.Steps.Select(s => s.RelayIds.ToArray()));

        wizard.Toggle(3, 3, "Relay 3", Board);
        Assert.Empty(wizard.Editor.Steps);
        Assert.True(wizard.FitsBoard(Board + 1));
    }

    private static int[] RelayIds(GroupWizard wizard)
    {
        return [.. wizard.Editor.Relays.Select(r => r.RelayId)];
    }

    private static GroupWizard AtStepTwo()
    {
        var wizard = new GroupWizard { Name = "Fan heater" };
        Assert.True(wizard.GoOn(1));
        return wizard;
    }
}
