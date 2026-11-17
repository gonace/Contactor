using Contactor.Boards;

namespace Contactor.Web.Features.Inputs;

/// <summary>
/// What a page shows of each analog input's value, so a new reading redraws the page only when it changes a text the
/// page shows. An analog input's reading moves by a count or two on most polls; most of those moves change no digit
/// shown. Used on the page's own thread (its <c>InvokeAsync</c>), as it draws: a reading compared there can't slip in
/// between the page reading a board's state and keeping what it showed.
/// </summary>
public sealed class ShownAnalogValues
{
    private readonly bool _showsVolts;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (AnalogInputView Input, AnalogValue Value)> _shown = [];

    /// <param name="showsVolts">
    /// Whether the page shows the voltage the board reads as well as the value, as the analog input's own page does.
    /// </param>
    public ShownAnalogValues(bool showsVolts)
    {
        _showsVolts = showsVolts;
    }

    /// <summary>Forgets every value shown; for the start of a page's drawing, which shows them again.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _shown.Clear();
        }
    }

    /// <summary>What <paramref name="input"/> shows now, kept as what the page shows.</summary>
    /// <param name="ability">Its board's analog inputs; null for a board that has none (or none any more).</param>
    /// <param name="state">Its board's state now.</param>
    public AnalogValue Show(AnalogInputView input, AnalogAbility? ability, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = AnalogValueText.Show(input.Scale, ability, state, input.Channel);
        lock (_lock)
        {
            _shown[input.Id] = (input, value);
        }

        return value;
    }

    /// <summary>
    /// Whether a board's state now changes a text shown for one of its analog inputs. Call it in the app's language
    /// (<c>AppLanguage.Use</c>): the numbers are in its format. An input the page doesn't show changes nothing.
    /// </summary>
    /// <param name="boardId">The board.</param>
    /// <param name="ability">The board's analog inputs.</param>
    /// <param name="state">The board's state now.</param>
    public bool Changes(int boardId, AnalogAbility? ability, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            foreach (var (input, shown) in _shown.Values)
            {
                if (input.BoardId == boardId && !Same(shown, AnalogValueText.Show(input.Scale, ability, state, input.Channel)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The texts only: the voltage itself moves with every count, its text and the value's much less often.
    private bool Same(AnalogValue shown, AnalogValue now)
    {
        return shown.State == now.State && shown.Text == now.Text && shown.Unit == now.Unit && (!_showsVolts || shown.VoltsText == now.VoltsText);
    }
}
