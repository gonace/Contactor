namespace Contactor.Boards.Advantech;

/// <summary>
/// The Advantech ADAM modules in the board catalogue. Modbus TCP on port 502 and no login: Modbus has none, and the
/// module's password guards only its configuration. From Advantech's manuals only, not yet checked on a real module.
/// </summary>
public static class AdamModels
{
    /// <summary>
    /// The ADAM-6060: 6 relays (Form A, 120 VAC 0.5 A, 30 VDC 1 A) and 6 digital inputs. "ADAM-6000 Series User
    /// Manual", section 5.5 and appendix B.2.8.
    /// </summary>
    public static BoardModel Adam6060 { get; } = Create("advantech-adam-6060", "ADAM-6060", relays: 6, inputs: 6);

    /// <summary>
    /// The ADAM-6066: 6 power relays (Form A, 250 VAC 5 A, 30 VDC 3 A) and 6 digital inputs, the same map as the
    /// ADAM-6060. "ADAM-6000 Series User Manual", section 5.6 and appendix B.2.8.
    /// </summary>
    public static BoardModel Adam6066 { get; } = Create("advantech-adam-6066", "ADAM-6066", relays: 6, inputs: 6);

    public static IReadOnlyList<BoardModel> All { get; } = [Adam6060, Adam6066];

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsAdam(BoardModel model)
    {
        return All.Contains(model);
    }

    // No pulse: the modules' pulse output is a pulse train set up in registers, not one pulse of a given length.
    private static BoardModel Create(string id, string name, int relays, int inputs)
    {
        return new BoardModel(
            id,
            AdvantechBrand.Name,
            name,
            new BoardCapabilities(relays, inputs, ReportsChanges: false),
            AdamApi.DefaultPort,
            LoginMaxLength: 0);
    }
}
