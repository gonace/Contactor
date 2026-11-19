namespace Contactor.Boards.Velleman;

/// <summary>
/// One of the 14 alarms of a relay's week schedule on the VM208 (<c>struct Shedule</c> in the firmware's
/// <c>include/ChannelShedule.hpp</c>): on its day, at its time, the board turns the relay on (or off) by itself while
/// it is enabled. Each relay has one "on" and one "off" alarm per day of the week. <see cref="Vm208Api.AlarmsPath"/>
/// gives them in the firmware's order: the 7 "on" alarms, Sunday first, then the 7 "off" alarms, Sunday first
/// (<see cref="IndexOf"/>).
/// </summary>
/// <param name="DayOfWeek">The day, as the firmware counts it (<c>tm_wday</c>): 0 = Sunday … 6 = Saturday.</param>
/// <param name="Hour">The hour, 0–23.</param>
/// <param name="Minute">The minute, 0–59.</param>
/// <param name="TurnsOn">True for an "on" alarm, false for an "off" alarm.</param>
/// <param name="Enabled">Whether the board runs it.</param>
public sealed record Vm208Alarm(int DayOfWeek, int Hour, int Minute, bool TurnsOn, bool Enabled)
{
    /// <summary>
    /// Where the alarm for <paramref name="dayOfWeek"/> stands among a relay's 14: its day for an "on" alarm, its day
    /// + 7 for an "off" alarm (<c>ChannelShedule::getShedule</c>, <c>src/ChannelShedule.cpp</c>).
    /// </summary>
    public static int IndexOf(int dayOfWeek, bool turnsOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dayOfWeek, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dayOfWeek, 6);
        return dayOfWeek + (turnsOn ? 0 : 7);
    }

    /// <summary>
    /// Whether <paramref name="alarms"/> is a relay's whole schedule as the firmware keeps it: 14 alarms, each in its
    /// place (<see cref="IndexOf"/>), with a time of day the board's own page can set.
    /// </summary>
    public static bool IsSchedule(IReadOnlyList<Vm208Alarm> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        if (alarms.Count != Vm208Api.AlarmCount)
        {
            return false;
        }

        for (var index = 0; index < alarms.Count; index++)
        {
            if (alarms[index] is not { } alarm
                || alarm.DayOfWeek != index % 7
                || alarm.TurnsOn != index < 7
                || alarm.Hour is < 0 or > 23
                || alarm.Minute is < 0 or > 59)
            {
                return false;
            }
        }

        return true;
    }
}
