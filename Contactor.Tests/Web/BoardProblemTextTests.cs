using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.Boards;

namespace Contactor.Tests.Web;

/// <summary>Why a board refuses the connection, in words: "password" words for a password alone, "login" words else.</summary>
public sealed class BoardProblemTextTests
{
    private static readonly BoardProblemText Problem = new(TestTexts.Localizer);

    public static TheoryData<ConnectionProblem, string, string> English { get; } = new()
    {
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484), "The board at this address is an ETH484-B, not an ETH008-B.", "Wrong model" },
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008), "The board at this address is another model, not an ETH008-B.", "Wrong model" },
        { new(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "The board asks for a password.", "Needs a password" },
        { new(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "The board asks for a user name and password.", "Needs a login" },
        { new(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "The password is wrong.", "Wrong password" },
        { new(ConnectionProblemKind.LoginRefused, Vm204Model.Model), "The user name or password is wrong.", "Wrong login" },
    };

    public static TheoryData<ConnectionProblem, string, string> Swedish { get; } = new()
    {
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484), "Kortet på den här adressen är ett ETH484-B, inte ett ETH008-B.", "Fel modell" },
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008), "Kortet på den här adressen är en annan modell, inte ett ETH008-B.", "Fel modell" },
        { new(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "Kortet vill ha ett lösenord.", "Behöver lösenord" },
        { new(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "Kortet vill ha ett användarnamn och lösenord.", "Behöver inloggning" },
        { new(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "Lösenordet är fel.", "Fel lösenord" },
        { new(ConnectionProblemKind.LoginRefused, Vm204Model.Model), "Användarnamnet eller lösenordet är fel.", "Fel inloggning" },
    };

    [Theory]
    [MemberData(nameof(English))]
    public void Should_SayWhy_InEnglish(ConnectionProblem problem, string longText, string shortText)
    {
        Assert.Equal(longText, Problem.Long(problem));
        Assert.Equal(shortText, Problem.Short(problem));
    }

    [Theory]
    [MemberData(nameof(Swedish))]
    public void Should_SayWhy_InSwedish(ConnectionProblem problem, string longText, string shortText)
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal(longText, Problem.Long(problem));
        Assert.Equal(shortText, Problem.Short(problem));
    }

    // Settings can't change a board's model: another one is added again, but only when the app knows which.
    [Fact]
    public void Should_SayToAddItAgain_OnlyForAModelTheAppKnows()
    {
        var found = new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484);

        Assert.Equal("Add it again as an ETH484-B with 'Add a board', then remove this one.", Problem.AddAgain(found));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008)));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008)));
        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Lägg till det igen som ett ETH484-B med \"Lägg till reläkort\" och ta sedan bort det här.", Problem.AddAgain(found));
        }
    }
}
