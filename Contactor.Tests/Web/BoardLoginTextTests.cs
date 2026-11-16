using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.Boards;
using Contactor.Web.Resources;

namespace Contactor.Tests.Web;

/// <summary>A board's login in words: the same English the board libraries' codes have as their ToString.</summary>
public sealed class BoardLoginTextTests
{
    private static readonly BoardLoginText Login = new(TestTexts.For<BoardsTexts>());

    public static TheoryData<LoginProblem> Problems { get; } =
    [
        new LoginProblem(LoginProblemKind.TooLong, LoginField.UserName, 8),
        new LoginProblem(LoginProblemKind.TooLong, LoginField.Password, 31),
        new LoginProblem(LoginProblemKind.PlainCharacters, LoginField.UserName),
        new LoginProblem(LoginProblemKind.PlainCharacters, LoginField.Password),
        new LoginProblem(LoginProblemKind.ListedCharacters, LoginField.UserName, Symbols: Vm204Login.SymbolList),
        new LoginProblem(LoginProblemKind.ListedCharacters, LoginField.Password, Symbols: Vm204Login.SymbolList),
        new LoginProblem(LoginProblemKind.TooLongTogether, MaxLength: 48),
        new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: "Devantech"),
        new LoginProblem(LoginProblemKind.PasswordTooLong, MaxLength: 32),
        new LoginProblem(LoginProblemKind.PasswordCharacters),
        new LoginProblem(LoginProblemKind.NoLoginWebPages, Board: "VM208"),
        new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: "Waveshare"),
    ];

    [Theory]
    [MemberData(nameof(Problems))]
    public void Should_SayAProblem_AsItsEnglish(LoginProblem problem)
    {
        Assert.Equal(problem.ToString(), Login.Problem(problem));
    }

    [Fact]
    public void Should_HaveATestForEveryKindOfProblem()
    {
        Assert.Equal(Enum.GetValues<LoginProblemKind>().Order(), Problems.Select(p => p.Data.Kind).Distinct().Order());
    }

    [Fact]
    public void Should_SayTheBoardsOwnProblems_InTheirOldEnglish()
    {
        Assert.Equal("The Devantech board takes a password only, no user name.", Login.Problem(new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: "Devantech")));
        Assert.Equal("The VM208 takes no login: its password only protects its own web pages.", Login.Problem(new LoginProblem(LoginProblemKind.NoLoginWebPages, Board: "VM208")));
        Assert.Equal("The Waveshare board takes no login: its password only protects its own web page.", Login.Problem(new LoginProblem(LoginProblemKind.NoLoginWebPage, Board: "Waveshare")));
    }

    [Fact]
    public void Should_SayEachModelsLoginHint()
    {
        Assert.Equal("The same as on the board's own web page. At most 8 characters: letters a–z, digits and plain symbols.", Login.Hint(Vm201Model.Model));
        Assert.Equal($"The same as on the board's own web page. At most 31 characters, 48 for both together: letters a–z, digits and {Vm204Login.SymbolList}.", Login.Hint(Vm204Model.Model));
        Assert.Equal("The TCP password from the board's configuration page, not the login of its web page. At most 32 characters: letters, digits and plain symbols, no spaces.", Login.Hint(DevantechModels.Eth008));
    }
}
