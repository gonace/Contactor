using System.Globalization;
using Contactor.Boards;

namespace Contactor.Web.Features.Setup;

/// <summary>The setup guide's texts that depend on numbers.</summary>
public static class SetupText
{
    /// <summary>What a model has, for the list of boards: "8 relays · 1 input", "4 relays · 4 inputs · 1 analog input".</summary>
    public static string Capabilities(BoardCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var parts = new List<string> { Count(capabilities.RelayCount, "relay", "relays") };
        if (capabilities.InputCount > 0)
        {
            parts.Add(Count(capabilities.InputCount, "input", "inputs"));
        }

        if (capabilities.AnalogInputCount > 0)
        {
            parts.Add(Count(capabilities.AnalogInputCount, "analog input", "analog inputs"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>How many relays are in use and hidden, for the summary: "6 in use, 2 hidden", "8 in use", "None in use".</summary>
    public static string RelaysInUse(int inUse, int hidden)
    {
        var used = inUse == 0 ? "None in use" : $"{inUse.ToString(CultureInfo.InvariantCulture)} in use";
        return hidden == 0 ? used : $"{used}, {hidden.ToString(CultureInfo.InvariantCulture)} hidden";
    }

    private static string Count(int count, string one, string many)
    {
        return $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)}";
    }
}
