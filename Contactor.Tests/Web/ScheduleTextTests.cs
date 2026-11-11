using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

public sealed class ScheduleTextTests
{
    [Theory]
    [InlineData(ScheduleDays.All, "Varje dag", "Varje dag")]
    [InlineData(ScheduleDays.Weekdays, "Mån–Fre", "Måndag till fredag")]
    [InlineData(ScheduleDays.Saturday | ScheduleDays.Sunday, "Lör–Sön", "Lördag och söndag")]
    [InlineData(ScheduleDays.Saturday, "Lördag", "Lördag")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Wednesday | ScheduleDays.Friday, "Mån, Ons, Fre", "Måndag, onsdag och fredag")]
    [InlineData(ScheduleDays.Tuesday | ScheduleDays.Sunday, "Tis, Sön", "Tisdag och söndag")]
    [InlineData(ScheduleDays.Weekdays | ScheduleDays.Saturday, "Mån–Lör", "Måndag till lördag")]
    [InlineData(ScheduleDays.Wednesday | ScheduleDays.Thursday | ScheduleDays.Friday, "Ons–Fre", "Onsdag till fredag")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Tuesday, "Mån, Tis", "Måndag och tisdag")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Thursday, "Mån, Tis, Tor", "Måndag, tisdag och torsdag")]
    public void Should_NameTheDays(ScheduleDays days, string shortForm, string longForm)
    {
        Assert.Equal(shortForm, ScheduleText.ShortDays(days));
        Assert.Equal(longForm, ScheduleText.LongDays(days));
    }

    [Fact]
    public void Should_SummarizeInPlainSwedish()
    {
        Assert.Equal(
            "Måndag till fredag: reläet slås på 06:30 och stängs av 08:00.",
            ScheduleText.Summary(ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0)));
    }

    [Fact]
    public void Should_SayTheNextDay_WhenTheScheduleRunsOverMidnight()
    {
        Assert.Equal(
            "Lördag: reläet slås på 22:00 och stängs av 01:00 dagen efter.",
            ScheduleText.Summary(ScheduleDays.Saturday, new TimeOnly(22, 0), new TimeOnly(1, 0)));
    }

    [Fact]
    public void Should_SayNoDaysAreChosen()
    {
        Assert.Equal("Inga dagar valda.", ScheduleText.Summary(ScheduleDays.None, new TimeOnly(6, 0), new TimeOnly(7, 0)));
    }

    [Fact]
    public void Should_WriteTheEventLogForm()
    {
        Assert.Equal("mån–fre 06:30–08:00", ScheduleText.Inline(ScheduleDays.Weekdays, new TimeOnly(6, 30), new TimeOnly(8, 0)));
    }
}
