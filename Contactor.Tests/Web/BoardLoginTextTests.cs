using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Web.Features.Boards;

namespace Contactor.Tests.Web;

/// <summary>A board's login in words: the same English the board libraries' codes have as their ToString.</summary>
public sealed class BoardLoginTextTests
{
    private static readonly BoardLoginText Login = new(TestTexts.Localizer);

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
        new LoginProblem(LoginProblemKind.CommunityLength, MaxLength: 12, MinLength: 4),
        new LoginProblem(LoginProblemKind.CommunityMissing),
    ];

    [Fact]
    public void Should_HaveATestForEveryKindOfProblem()
    {
        Assert.Equal(Enum.GetValues<LoginProblemKind>().Order(), Problems.Select(p => p.Data.Kind).Distinct().Order());
    }

    [Fact]
    public void Should_SayEveryKindOfLoginHint()
    {
        Assert.All(Enum.GetValues<LoginHintKind>(), kind => Assert.False(string.IsNullOrWhiteSpace(Login.Hint(new BoardModel("x", "X", "Y", new BoardCapabilities(1, 0, false), 1, 8, new LoginHint(kind, 8))))));
        Assert.NotEqual(
            Login.Hint(new BoardModel("x", "X", "Y", new BoardCapabilities(1, 0, false), 1, 8, new LoginHint(LoginHintKind.TcpPassword, 8), PasswordOnly: true)),
            Login.Hint(new BoardModel("x", "X", "Y", new BoardCapabilities(1, 0, false), 1, 8, new LoginHint(LoginHintKind.SnmpCommunity, 8), PasswordOnly: true)));
    }
}
