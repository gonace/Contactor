using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>"New group" between its steps: one start order, and taps that arrive after the page moved on.</summary>
public sealed class GroupWizardTests
{
    [Fact]
    public void Should_KeepStepTwosNumbers_InTheOrderStepThreeChose()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(3, "Relay 3");
        wizard.Toggle(1, "Relay 1");
        Assert.True(wizard.GoOn(2));

        wizard.Move(1, -1);
        Assert.True(wizard.Back(3));

        Assert.Equal((1, 2), (wizard.OrderOf(1), wizard.OrderOf(3)));
        Assert.Equal([1, 3], wizard.Draft.Members.Select(m => m.RelayId));
    }

    [Fact]
    public void Should_PutARelayPickedAgainLast_KeepingItsNameAndWaits()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(3, "Relay 3");
        wizard.Toggle(1, "Relay 1");
        wizard.Toggle(5, "Relay 5");
        wizard.Members[0].RoleName = "Fan";
        wizard.Members[0].RunOnSec = 300;

        wizard.Toggle(3, "Relay 3");
        wizard.Toggle(3, "Relay 3");

        Assert.Equal([1, 5, 3], wizard.Members.Select(m => m.RelayId));
        Assert.Equal(3, wizard.OrderOf(3));
        Assert.Equal(("Fan", 300), (wizard.Members[2].RoleName, wizard.Members[2].RunOnSec));
    }

    [Fact]
    public void Should_IgnoreASecondTap_ThatArrivesAfterThePageMovedOn()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, "Relay 1");
        wizard.Toggle(2, "Relay 2");

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

        wizard.Toggle(1, "Relay 1");
        Assert.False(wizard.GoOn(2));
        wizard.Toggle(2, "Relay 2");
        Assert.True(wizard.GoOn(2));

        wizard.Members[1].RoleName = " ";
        Assert.False(wizard.GoOn(3));
        wizard.Members[1].RoleName = " Heat ";
        Assert.True(wizard.GoOn(3));

        Assert.False(wizard.GoOn(4));
        Assert.Equal(GroupWizard.StepCount, wizard.Step);
        Assert.Equal("Heat", wizard.Draft.Members[1].RoleName);
    }

    [Fact]
    public void Should_IgnoreMovesOutsideTheList()
    {
        var wizard = AtStepTwo();
        wizard.Toggle(1, "Relay 1");
        wizard.Toggle(2, "Relay 2");

        wizard.Move(0, -1);
        wizard.Move(1, 1);
        wizard.Move(5, -1);
        wizard.Move(-1, 1);

        Assert.Equal([1, 2], wizard.Members.Select(m => m.RelayId));
    }

    [Fact]
    public void Should_CutALongRelayNameToARoleName()
    {
        var wizard = AtStepTwo();

        wizard.Toggle(1, new string('Å', 60));

        Assert.Equal(RelayGroupMember.RoleNameMaxLength, wizard.Members[0].RoleName.Length);
    }

    private static GroupWizard AtStepTwo()
    {
        var wizard = new GroupWizard { Name = "Fan heater" };
        Assert.True(wizard.GoOn(1));
        return wizard;
    }
}
