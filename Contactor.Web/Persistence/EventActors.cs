using System.Globalization;

namespace Contactor.Web.Persistence;

/// <summary>Actors that are not a person.</summary>
public static class EventActors
{
    public const string Schedule = "schedule";
    public const string AutoOff = "auto-off";
    public const string External = "external";
    public const string System = "system";

    private const string InputPrefix = "input:";

    /// <summary>An input that switched a relay or a group by its actions, e.g. "input:1".</summary>
    public static string ForInput(int inputId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{InputPrefix}{inputId}");
    }

    /// <summary>Whether the actor is an input (<see cref="ForInput"/>), and which.</summary>
    public static bool TryInput(string? actor, out int inputId)
    {
        inputId = 0;
        return actor is not null
            && actor.StartsWith(InputPrefix, StringComparison.Ordinal)
            && int.TryParse(actor.AsSpan(InputPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out inputId)
            && inputId > 0;
    }
}
