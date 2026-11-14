using System.Globalization;

namespace Contactor.Boards.Vm201;

/// <summary>
/// The time of a VM201 pulse (<see cref="Vm201Command.Pulse"/>): a number 1–99 and a unit, 's', 'm' or 'h'. So a
/// length goes out exactly only as whole seconds up to 99 s, else whole minutes up to 99 min, else whole hours up to
/// 99 h; 100 s, for one, can't be sent, though <see cref="Ability"/> allows it.
/// </summary>
public static class Vm201Pulse
{
    /// <summary>The largest number the board takes, in any unit.</summary>
    public const int MaxTime = 99;

    public const byte Seconds = (byte)'s';
    public const byte Minutes = (byte)'m';
    public const byte Hours = (byte)'h';

    /// <summary>
    /// From 1 s to 99 h in steps of 1 s, the closest a <see cref="PulseAbility"/> comes: not every length in it can
    /// be sent (<see cref="TryEncode"/>).
    /// </summary>
    public static PulseAbility Ability { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromHours(MaxTime), TimeSpan.FromSeconds(1));

    private static readonly (byte Unit, TimeSpan Size)[] Units =
    [
        (Seconds, TimeSpan.FromSeconds(1)),
        (Minutes, TimeSpan.FromMinutes(1)),
        (Hours, TimeSpan.FromHours(1)),
    ];

    /// <summary>
    /// The number and unit that send <paramref name="length"/> exactly, in the smallest unit that can; false when
    /// none can.
    /// </summary>
    public static bool TryEncode(TimeSpan length, out byte time, out byte unit)
    {
        foreach (var (u, size) in Units)
        {
            if (length.Ticks % size.Ticks == 0 && length.Ticks / size.Ticks is >= 1 and <= MaxTime)
            {
                time = (byte)(length.Ticks / size.Ticks);
                unit = u;
                return true;
            }
        }

        time = 0;
        unit = 0;
        return false;
    }

    /// <summary>The length a number and unit stand for, or null when the board can't take them.</summary>
    public static TimeSpan? Decode(byte time, byte unit)
    {
        if (time is < 1 or > MaxTime)
        {
            return null;
        }

        foreach (var (u, size) in Units)
        {
            if (u == unit)
            {
                return size * time;
            }
        }

        return null;
    }

    /// <summary>"2 s", "5 min", "3 h"; for a number or unit the board can't take, both as they came.</summary>
    public static string Describe(byte time, byte unit)
    {
        var number = time.ToString(CultureInfo.InvariantCulture);
        if (Decode(time, unit) is null)
        {
            var letter = unit is >= 0x20 and < 0x7F ? ((char)unit).ToString() : $"0x{unit:X2}";
            return $"{number} {letter}";
        }

        return unit switch
        {
            Seconds => $"{number} s",
            Minutes => $"{number} min",
            _ => $"{number} h",
        };
    }
}
