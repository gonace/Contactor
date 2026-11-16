using Contactor.Boards;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Why a board refuses the connection, in words, in the app's language: the long text for the setup guide, Settings and
/// the Relays page, and the short one for a board's row and the side menu. The board libraries only report the problem
/// (<see cref="ConnectionProblem"/>). A board with a password alone gets "password" words, one with a user name "login"
/// words. Never a secret: the texts hold model names only.
/// </summary>
public sealed class BoardProblemText
{
    private readonly IStringLocalizer<Texts> _texts;

    public BoardProblemText(IStringLocalizer<Texts> texts)
    {
        _texts = texts;
    }

#pragma warning disable CA1720 // "Long" and "Short" are how long the text is, not types.

    /// <summary>e.g. "The board at this address is an ETH484-B, not an ETH008-B." or "The password is wrong."</summary>
    public string Long(ConnectionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var passwordOnly = problem.Expected.PasswordOnly;
        return problem.Kind switch
        {
            ConnectionProblemKind.WrongModel when problem.Found is { } found => _texts["Boards.Problem.WrongModel", found.ModelName, problem.Expected.ModelName],
            ConnectionProblemKind.WrongModel => _texts["Boards.Problem.WrongModel.Unknown", problem.Expected.ModelName],
            ConnectionProblemKind.LoginNeeded => _texts[passwordOnly ? "Boards.Problem.PasswordNeeded" : "Boards.Problem.LoginNeeded"],
            _ => _texts[passwordOnly ? "Boards.Problem.WrongPassword" : "Boards.Problem.WrongLogin"],
        };
    }

    /// <summary>e.g. "Wrong model", "Needs a password" or "Wrong login".</summary>
    public string Short(ConnectionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var passwordOnly = problem.Expected.PasswordOnly;
        return problem.Kind switch
        {
            ConnectionProblemKind.WrongModel => _texts["Boards.Problem.Short.WrongModel"],
            ConnectionProblemKind.LoginNeeded => _texts[passwordOnly ? "Boards.Problem.Short.PasswordNeeded" : "Boards.Problem.Short.LoginNeeded"],
            _ => _texts[passwordOnly ? "Boards.Problem.Short.WrongPassword" : "Boards.Problem.Short.WrongLogin"],
        };
    }
#pragma warning restore CA1720

    /// <summary>
    /// Where a model can't be changed (Settings): "Add it again as an ETH484-B with 'Add a board', then remove this
    /// one."; null unless another model the app knows is at the address.
    /// </summary>
    public string? AddAgain(ConnectionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem is { Kind: ConnectionProblemKind.WrongModel, Found: { } found }
            ? _texts["Boards.Problem.WrongModel.AddAgain", found.ModelName, _texts["Common.AddBoard"].Value].Value
            : null;
    }
}
