using Contactor.Web.Features.Schedules;
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
        Assert.Equal(shortForm, ScheduleText.ShortDays(days));
        Assert.Equal(longForm, ScheduleText.LongDays(days));
    }

    [Fact]
    public void Should_SummarizeInPlainWords()
    {
        Assert.Equal(
            "Monday to Friday: the relay turns on at 06:30 and off at 08:00.",
            ScheduleText.Summary(ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0)));
    }

    [Fact]
    public void Should_SayTheNextDay_WhenTheScheduleRunsOverMidnight()
    {
        Assert.Equal(
            "Saturday: the relay turns on at 22:00 and off at 01:00 the next day.",
            ScheduleText.Summary(ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0)));
    }

    [Fact]
    public void Should_SayNoDaysAreChosen()
    {
        Assert.Equal("No days chosen.", ScheduleText.Summary(ScheduleDays.None, new TimeOnly(6, 0), new TimeOnly(7, 0)));
    }

    [Fact]
    public void Should_WriteTheEventLogForm()
    {
        Assert.Equal("Mon–Fri 06:30–08:00", ScheduleText.Inline(ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0)));
    }
}
