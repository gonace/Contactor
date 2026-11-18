using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
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
        { new(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4), "The board at this address has 4 relays, not 2.", "Wrong model" },
        { new(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.OneRelay, FoundRelayCount: 1), "The board at this address has 1 relay, not 2.", "Wrong model" },
        { new(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, FoundRelayCount: 6), "The board at this address has 6 relays, not 2.", "Wrong model" },
        { new(ConnectionProblemKind.CoverProfile, ShellyModels.TwoRelays), "The board is set up to drive a cover, not relays. Set its profile to \"switch\" on its own web page, then check again.", "Set up for a cover" },
        { new(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "The board asks for a password.", "Needs a password" },
        { new(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "The board asks for a user name and password.", "Needs a login" },
        { new(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "The password is wrong.", "Wrong password" },
        { new(ConnectionProblemKind.LoginRefused, Vm204Model.Model), "The user name or password is wrong.", "Wrong login" },
        { new(ConnectionProblemKind.FirmwareTooOld, ShellyModels.TwoRelays, MinFirmware: "1.7.0"), "The board's firmware is too old. Update it to 1.7.0 or later on the board's own web page.", "Old firmware" },
        { new(ConnectionProblemKind.FirmwareTooOld, ShellyModels.TwoRelays), "The board's firmware is too old. Update it on the board's own web page.", "Old firmware" },
        { new(ConnectionProblemKind.TooManyTries, ShellyModels.TwoRelays), "Too many wrong passwords. Check the password, then wait a few minutes: Contactor tries again by itself.", "Too many tries" },
        { new(ConnectionProblemKind.TooManyTries, Vm204Model.Model), "Too many wrong logins. Check the user name and password, then wait a few minutes: Contactor tries again by itself.", "Too many tries" },
    };

    public static TheoryData<ConnectionProblem, string, string> Swedish { get; } = new()
    {
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484), "Kortet på den här adressen är ett ETH484-B, inte ett ETH008-B.", "Fel modell" },
        { new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008), "Kortet på den här adressen är en annan modell, inte ett ETH008-B.", "Fel modell" },
        { new(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4), "Kortet på den här adressen har 4 reläer, inte 2.", "Fel modell" },
        { new(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.OneRelay, FoundRelayCount: 1), "Kortet på den här adressen har 1 relä, inte 2.", "Fel modell" },
        { new(ConnectionProblemKind.CoverProfile, ShellyModels.TwoRelays), "Kortet är inställt på att styra en rullgardin eller markis, inte reläer. Ställ in dess profil på \"switch\" på kortets egen webbsida och kontrollera sedan igen.", "Inställt för rullgardin" },
        { new(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "Kortet vill ha ett lösenord.", "Behöver lösenord" },
        { new(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "Kortet vill ha ett användarnamn och lösenord.", "Behöver inloggning" },
        { new(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "Lösenordet är fel.", "Fel lösenord" },
        { new(ConnectionProblemKind.LoginRefused, Vm204Model.Model), "Användarnamnet eller lösenordet är fel.", "Fel inloggning" },
        { new(ConnectionProblemKind.FirmwareTooOld, ShellyModels.TwoRelays, MinFirmware: "1.7.0"), "Kortets firmware är för gammal. Uppdatera den till 1.7.0 eller senare på kortets egen webbsida.", "Gammal firmware" },
        { new(ConnectionProblemKind.FirmwareTooOld, ShellyModels.TwoRelays), "Kortets firmware är för gammal. Uppdatera den på kortets egen webbsida.", "Gammal firmware" },
        { new(ConnectionProblemKind.TooManyTries, ShellyModels.TwoRelays), "För många fel lösenord. Kontrollera lösenordet och vänta sedan några minuter: Contactor försöker igen av sig själv.", "För många försök" },
        { new(ConnectionProblemKind.TooManyTries, Vm204Model.Model), "För många fel inloggningar. Kontrollera användarnamnet och lösenordet och vänta sedan några minuter: Contactor försöker igen av sig själv.", "För många försök" },
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

    // Another number of relays names the counts, never the models, and is added again only with an entry for its count.
    [Fact]
    public void Should_SayToAddItAgain_WithTheRelaysFound_ForAnotherNumberOfRelays()
    {
        var four = new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4);

        Assert.Equal("Add it again with 'Add a board' and choose the one with 4 relays, then remove this one.", Problem.AddAgain(four));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, FoundRelayCount: 6)));
        Assert.Equal("The board at this address has 4 relays, not 2.", four.ToString());
        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Lägg till det igen med \"Lägg till reläkort\", välj det med 4 reläer och ta sedan bort det här.", Problem.AddAgain(four));
        }
    }

    // A new kind of problem gets its words in both languages, and a test here.
    [Fact]
    public void Should_HaveATestForEveryKindOfProblem_InBothLanguages()
    {
        Assert.Equal(Enum.GetValues<ConnectionProblemKind>().Order(), English.Select(p => p.Data.Item1.Kind).Distinct().Order());
        Assert.Equal(Enum.GetValues<ConnectionProblemKind>().Order(), Swedish.Select(p => p.Data.Item1.Kind).Distinct().Order());
    }

    // Only the problems about the login open the login fields: a wrong model or old firmware isn't fixed there.
    [Theory]
    [InlineData(ConnectionProblemKind.WrongModel, false)]
    [InlineData(ConnectionProblemKind.LoginNeeded, true)]
    [InlineData(ConnectionProblemKind.LoginRefused, true)]
    [InlineData(ConnectionProblemKind.FirmwareTooOld, false)]
    [InlineData(ConnectionProblemKind.TooManyTries, true)]
    [InlineData(ConnectionProblemKind.CoverProfile, false)]
    public void Should_SayWhetherAProblemIsAboutTheLogin(ConnectionProblemKind kind, bool aboutLogin)
    {
        Assert.Equal(aboutLogin, new ConnectionProblem(kind, ShellyModels.OneRelay).IsAboutLogin);
    }

    // Settings can't change a board's model: another one is added again, but only when the app knows which.
    [Fact]
    public void Should_SayToAddItAgain_OnlyForAModelTheAppKnows()
    {
        var found = new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484);

        Assert.Equal("Add it again as an ETH484-B with 'Add a board', then remove this one.", Problem.AddAgain(found));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008)));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008)));
        Assert.Null(Problem.AddAgain(new ConnectionProblem(ConnectionProblemKind.CoverProfile, ShellyModels.TwoRelays)));
        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Lägg till det igen som ett ETH484-B med \"Lägg till reläkort\" och ta sedan bort det här.", Problem.AddAgain(found));
        }
    }
}
