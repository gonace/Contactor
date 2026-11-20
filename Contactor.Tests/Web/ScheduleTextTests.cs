using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class ScheduleTextTests
{
    [Theory]
    [InlineData(ScheduleDays.All, "Every day", "Every day")]
    [InlineData(ScheduleDays.Weekdays, "Mon–Fri", "Monday to Friday")]
    [InlineData(ScheduleDays.Saturday | ScheduleDays.Sunday, "Sat–Sun", "Saturday and Sunday")]
    [InlineData(ScheduleDays.Saturday, "Saturday", "Saturday")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Wednesday | ScheduleDays.Friday, "Mon, Wed, Fri", "Monday, Wednesday and Friday")]
    [InlineData(ScheduleDays.Tuesday | ScheduleDays.Sunday, "Tue, Sun", "Tuesday and Sunday")]
    [InlineData(ScheduleDays.Weekdays | ScheduleDays.Saturday, "Mon–Sat", "Monday to Saturday")]
    [InlineData(ScheduleDays.Wednesday | ScheduleDays.Thursday | ScheduleDays.Friday, "Wed–Fri", "Wednesday to Friday")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Tuesday, "Mon, Tue", "Monday and Tuesday")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Thursday, "Mon, Tue, Thu", "Monday, Tuesday and Thursday")]
    public void Should_NameTheDays(ScheduleDays days, string shortForm, string longForm)
    {
        Assert.Equal(shortForm, TestTexts.Schedules.ShortDays(days));
        Assert.Equal(longForm, TestTexts.Schedules.LongDays(days));
    }

    [Fact]
    public void Should_WriteTheEventLogForm()
    {
        Assert.Equal("Mon–Fri 06:30–08:00", TestTexts.Schedules.Inline(ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0)));
    }
}
