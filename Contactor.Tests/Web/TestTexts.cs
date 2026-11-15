using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's texts for tests that use a text class on its own, outside the app: read from the same resource files,
/// in the culture the test runs in (English, <see cref="TestCulture"/>).
/// </summary>
internal static class TestTexts
{
    private static readonly ResourceManagerStringLocalizerFactory Factory =
        new(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);

    public static RelayStatusText RelayStatus { get; } = new(For<RelaysTexts>());

    public static BoardsText Boards { get; } = new(For<RelaysTexts>());

    public static ScheduleText Schedules { get; } = new(For<SchedulesTexts>());

    public static InputsText Inputs { get; } = new(For<InputsTexts>(), RelayStatus);

    public static EventText Events { get; } = new(For<EventsTexts>(), Schedules, Inputs);

    /// <summary>The Events screen's handler, for what it says without its database: the days' headings.</summary>
    public static EventsHandler EventsHandler { get; } = new(null!, null!, Events, For<EventsTexts>());

    public static GroupStatusText GroupStatus { get; } = new(For<GroupsTexts>(), RelayStatus, Events);

    public static GroupPlanText GroupPlan { get; } = new(For<GroupsTexts>(), Events);

    public static IStringLocalizer<T> For<T>() => new StringLocalizer<T>(Factory);
}
