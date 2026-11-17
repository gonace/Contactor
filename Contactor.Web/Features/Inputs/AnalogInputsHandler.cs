using System.Globalization;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Inputs;

/// <summary>An analog input as the screens show it.</summary>
/// <param name="Id">The analog input, for links and the database.</param>
/// <param name="Channel">Its analog input on the board: what the board's readings go by, and the number the screens show.</param>
/// <param name="Scale">Its unit and scale.</param>
public sealed record AnalogInputView(int Id, int Channel, string Name, bool IsActive, AnalogScale Scale)
{
    /// <summary>The analog input's board.</summary>
    public int BoardId { get; init; }
}

/// <summary>
/// What the analog input's screen saves: its name, "Active", unit, the unit's own text with "Other", and the scale's two
/// points, all four or none.
/// </summary>
public sealed record AnalogInputSettings(
    int Id,
    string Name,
    bool IsActive,
    string Unit,
    string UnitText,
    decimal? Volts1,
    decimal? Value1,
    decimal? Volts2,
    decimal? Value2);

/// <summary>
/// The boards' analog inputs: their names, units and scales. Their readings come from their board. Analog inputs above
/// their board's count, left from a board that had more, are left out.
/// </summary>
public sealed class AnalogInputsHandler
{
    /// <summary>The highest voltage a scale's point can have; no board reads more than a few volts.</summary>
    public const decimal MaxVolts = 100m;

    /// <summary>The largest value, either way, a scale's point can have.</summary>
    public const decimal MaxValue = 1_000_000m;

    /// <summary>The decimals a scale's voltages and values keep: a millivolt, and as fine as a value is ever shown.</summary>
    public const int PointDecimals = AnalogValueText.MaxDecimals;

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly RelayChanges _changes;
    private readonly IStringLocalizer<Texts> _texts;

    public AnalogInputsHandler(IDbContextFactory<ContactorDbContext> dbFactory, BoardRegistry registry, RelayChanges changes, IStringLocalizer<Texts> texts)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _changes = changes;
        _texts = texts;
    }

    /// <summary>Every analog input its board has, in order: by board, then channel.</summary>
    public async Task<IReadOnlyList<AnalogInputView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var inputs = await db.AnalogInputs.AsNoTracking().OrderBy(i => i.BoardId).ThenBy(i => i.Channel).ToListAsync(cancellationToken);
        return [.. inputs.Where(i => _registry.HasAnalogInput(i.BoardId, i.Channel)).Select(View)];
    }

    /// <summary>One analog input, or null for one its board doesn't have.</summary>
    public async Task<AnalogInputView?> GetAsync(int analogInputId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.AnalogInputs.AsNoTracking().SingleOrDefaultAsync(i => i.Id == analogInputId, cancellationToken);
        return input is null || !_registry.HasAnalogInput(input.BoardId, input.Channel) ? null : View(input);
    }

    /// <summary>
    /// Saves an analog input's settings. The scale's points are rounded to <see cref="PointDecimals"/> decimals first.
    /// Not logged. Returns null when saved, otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveAsync(AnalogInputSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = settings.Name?.Trim() ?? "";
        var unit = settings.Unit ?? AnalogUnits.None;
        var unitText = unit == AnalogUnits.Other ? settings.UnitText?.Trim() ?? "" : "";
        decimal?[] points = [Round(settings.Volts1), Round(settings.Value1), Round(settings.Volts2), Round(settings.Value2)];
        if (Check(name, unit, unitText, points) is { } problem)
        {
            return problem;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.AnalogInputs.SingleOrDefaultAsync(i => i.Id == settings.Id, cancellationToken);
        if (input is null || !_registry.HasAnalogInput(input.BoardId, input.Channel))
        {
            return _texts["Analog.Missing"];
        }

        (input.Name, input.IsActive, input.Unit, input.UnitText) = (name, settings.IsActive, unit, unitText);
        (input.Volts1, input.Value1, input.Volts2, input.Value2) = (points[0], points[1], points[2], points[3]);
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    private string? Check(string name, string unit, string unitText, decimal?[] points)
    {
        if (name.Length == 0)
        {
            return _texts["Inputs.Save.EnterName"];
        }

        if (name.Length > AnalogInput.NameMaxLength)
        {
            return _texts["Inputs.Save.NameTooLong", AnalogInput.NameMaxLength];
        }

        if (!AnalogUnits.All.Contains(unit))
        {
            return _texts["Analog.Save.ChooseUnit"];
        }

        if (unit == AnalogUnits.Other && unitText.Length == 0)
        {
            return _texts["Analog.Save.EnterUnit"];
        }

        if (unitText.Length > AnalogInput.UnitTextMaxLength)
        {
            return _texts["Analog.Save.UnitTooLong", AnalogInput.UnitTextMaxLength];
        }

        var filled = points.Count(p => p is not null);
        if (filled == 0)
        {
            // Without a scale the value is the voltage, which only volts can show.
            return AnalogUnits.IsVolts(unit) ? null : _texts["Analog.Save.NeedsScale"].Value;
        }

        if (filled < points.Length)
        {
            return _texts["Analog.Save.BothPoints"];
        }

        if (points[0] is not { } volts1 || points[2] is not { } volts2 || volts1 < 0m || volts1 > MaxVolts || volts2 < 0m || volts2 > MaxVolts)
        {
            return _texts["Analog.Save.VoltsRange", MaxVolts.ToString("N0", CultureInfo.CurrentCulture)];
        }

        if (volts1 == volts2)
        {
            return _texts["Analog.Save.SameVolts"];
        }

        if (Math.Abs(points[1]!.Value) > MaxValue || Math.Abs(points[3]!.Value) > MaxValue)
        {
            return _texts["Analog.Save.ValueRange", (-MaxValue).ToString("N0", CultureInfo.CurrentCulture), MaxValue.ToString("N0", CultureInfo.CurrentCulture)];
        }

        // One value at both points shows the same value at every voltage: always a mistake.
        return points[1] == points[3] ? _texts["Analog.Save.SameValues"].Value : null;
    }

    private static decimal? Round(decimal? value)
    {
        return value is { } number ? Math.Round(number, PointDecimals, MidpointRounding.AwayFromZero) : null;
    }

    private static AnalogInputView View(AnalogInput input)
    {
        return new AnalogInputView(input.Id, input.Channel, input.Name, input.IsActive, AnalogScale.Of(input)) { BoardId = input.BoardId };
    }
}
