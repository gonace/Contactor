using Contactor.Boards;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// A board's login in words, in the app's language: the hint under the login fields and why the board can't take a
/// login. The board libraries only report the rule (<see cref="LoginHint"/>) and the problem (<see cref="LoginProblem"/>).
/// </summary>
public sealed class BoardLoginText
{
    private readonly IStringLocalizer<BoardsTexts> _texts;

    public BoardLoginText(IStringLocalizer<BoardsTexts> texts)
    {
        _texts = texts;
    }

    /// <summary>
    /// The hint under the login fields: "The same as on the board's own web page. At most 8 characters: …", or for a
    /// password alone just its rule; null for a password alone without one.
    /// </summary>
    public string? Hint(BoardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rule = model.LoginHint is { } hint ? Rule(hint) : null;
        return model.PasswordOnly
            ? rule
            : _texts["Boards.Login.Hint", rule ?? _texts["Boards.Login.MaxLength", model.LoginMaxLength].Value];
    }

    /// <summary>Why the board can't take a login, e.g. "The user name can be at most 8 characters on the relay board."</summary>
    public string Problem(LoginProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var userName = problem.Field == LoginField.UserName;
        return problem.Kind switch
        {
            LoginProblemKind.TooLong => _texts[userName ? "Boards.Login.TooLong.UserName" : "Boards.Login.TooLong.Password", problem.MaxLength],
            LoginProblemKind.PlainCharacters => _texts[userName ? "Boards.Login.PlainCharacters.UserName" : "Boards.Login.PlainCharacters.Password"],
            LoginProblemKind.ListedCharacters => _texts[userName ? "Boards.Login.ListedCharacters.UserName" : "Boards.Login.ListedCharacters.Password", problem.Symbols],
            LoginProblemKind.TooLongTogether => _texts["Boards.Login.TooLongTogether", problem.MaxLength],
            LoginProblemKind.PasswordOnly => _texts["Boards.Login.PasswordOnly", problem.Board],
            LoginProblemKind.PasswordTooLong => _texts["Boards.Login.PasswordTooLong", problem.MaxLength],
            LoginProblemKind.PasswordCharacters => _texts["Boards.Login.PasswordCharacters"],
            LoginProblemKind.NoLoginWebPages => _texts["Boards.Login.NoLoginWebPages", problem.Board],
            LoginProblemKind.CommunityLength => _texts["Boards.Login.CommunityLength", problem.MinLength, problem.MaxLength],
            LoginProblemKind.CommunityMissing => _texts["Boards.Login.CommunityMissing"],
            _ => _texts["Boards.Login.NoLoginWebPage", problem.Board],
        };
    }

    private string Rule(LoginHint hint)
    {
        return hint.Kind switch
        {
            LoginHintKind.PlainSymbols => _texts["Boards.LoginHint.PlainSymbols", hint.MaxLength],
            LoginHintKind.ListedSymbols => _texts["Boards.LoginHint.ListedSymbols", hint.MaxLength, hint.TotalMaxLength, hint.Symbols],
            LoginHintKind.TcpPassword => _texts["Boards.LoginHint.TcpPassword", hint.MaxLength],
            _ => _texts["Boards.LoginHint.SnmpCommunity", hint.MinLength, hint.MaxLength, hint.FactoryValue],
        };
    }
}
