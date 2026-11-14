using Contactor.Boards;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// What a model's code needs from a board's row to build it (<see cref="BoardRegistration.Create"/>): which board it is,
/// whether it is the first (only the first takes the address and login in the model's settings section, e.g.
/// <c>Vm201:Host</c>, as install.sh writes them), and the address and login saved on its row, if any.
/// </summary>
/// <param name="BoardId">The board's row (<see cref="Board.Id"/>): the events of what only this model has are about it.</param>
/// <param name="IsFirst">Whether it is the first board in the list, the one the model's settings section is for.</param>
/// <param name="Endpoint">The address saved in the app, or null while the settings' applies.</param>
/// <param name="Login">The login saved in the app, or null while the settings' applies. A secret: never log it.</param>
public sealed record BoardSetup(int BoardId, bool IsFirst, BoardEndpoint? Endpoint, BoardLogin? Login)
{
    // The settings that say where a board is and how to log in to it: only the first board takes them from the section.
    private static readonly HashSet<string> AddressKeys = new(StringComparer.OrdinalIgnoreCase) { "Host", "Port", "Username", "Password" };

    /// <summary>The setup of the board on <paramref name="row"/>.</summary>
    public static BoardSetup From(Board row, bool isFirst)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new BoardSetup(row.Id, isFirst, SavedEndpoint(row), SavedLogin(row));
    }

    /// <summary>The address saved on the row, or null while the board's own settings apply.</summary>
    public static BoardEndpoint? SavedEndpoint(Board? row)
    {
        return row is { Host: { } host, Port: { } port } ? new BoardEndpoint(host, port) : null;
    }

    /// <summary>The login saved on the row, or null while the board's own settings apply.</summary>
    public static BoardLogin? SavedLogin(Board? row)
    {
        return row?.Username is { } username ? new BoardLogin(username, row.Password ?? "") : null;
    }

    /// <summary>
    /// The model's settings for this board: the whole section for the first board; for any other, the section without
    /// its address and login, which are the first board's, so that board starts at the model's default port with no
    /// address until one is saved for it.
    /// </summary>
    public IConfiguration Settings(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (IsFirst)
        {
            return section;
        }

        var values = section.AsEnumerable(makePathsRelative: true)
            .Where(p => p.Value is not null && !AddressKeys.Contains(p.Key.Split(ConfigurationPath.KeyDelimiter)[0]));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // The record's own text would print the login, password and all.
    public override string ToString()
    {
        return $"Board {BoardId}{(IsFirst ? " (first)" : "")}";
    }
}
