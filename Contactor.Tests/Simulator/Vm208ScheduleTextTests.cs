using Contactor.Boards.Velleman;
using Contactor.Simulator;

namespace Contactor.Tests.Simulator;

/// <summary>A VM208 relay's week schedule in plain words on the simulator's page.</summary>
public sealed class Vm208ScheduleTextTests
{
    [Fact]
    public void Should_SayNoAlarmIsOn_ForTheFactorySchedule()
    {
        Assert.Equal("No alarm on", Vm208ScheduleText.Describe(Vm208Simulator.FactorySchedule));
    }

    [Fact]
    public void Should_GroupTheDaysWithTheSameTimes_MondayFirst()
    {
        var alarms = Vm208Simulator.FactorySchedule.ToArray();
        for (var day = 1; day <= 5; day++)
        {
            Set(alarms, day, turnsOn: true, 6, 30);
            Set(alarms, day, turnsOn: false, 22, 0);
        }

        Set(alarms, 6, turnsOn: true, 8, 0);
        Set(alarms, 0, turnsOn: true, 8, 0);

        Assert.Equal("Mon–Fri on 06:30, off 22:00 · Sat, Sun on 08:00", Vm208ScheduleText.Describe(alarms));
    }

    [Fact]
    public void Should_ShowATimeTheBoardNeverRuns_AsItIs()
    {
        var alarms = Vm208Simulator.FactorySchedule.ToArray();
        Set(alarms, 3, turnsOn: false, 99, 0);

        Assert.Equal("Wed off 99:00", Vm208ScheduleText.Describe(alarms));
    }

    private static void Set(Vm208Alarm[] alarms, int day, bool turnsOn, int hour, int minute)
    {
        var index = Vm208Alarm.IndexOf(day, turnsOn);
        alarms[index] = alarms[index] with { Hour = hour, Minute = minute, Enabled = true };
    }
}
