using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Features.Setup;
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

    /// <summary>The texts, as the app reads them: <c>IStringLocalizer&lt;Texts&gt;</c>.</summary>
    public static IStringLocalizer<Texts> Localizer { get; } = new StringLocalizer<Texts>(Factory);

    public static RelayStatusText RelayStatus { get; } = new(Localizer);

    public static BoardsText Boards { get; } = new(Localizer);

    public static OwnRulesText OwnRules { get; } = new(Localizer, Boards);

    public static ScheduleText Schedules { get; } = new(Localizer);

    public static InputsText Inputs { get; } = new(Localizer, RelayStatus);

    public static EventText Events { get; } = new(Localizer, Schedules, Inputs);

    /// <summary>The Events screen's handler, for what it says without its database: the days' headings.</summary>
    public static EventsHandler EventsHandler { get; } = new(null!, null!, Events, Localizer);

    public static GroupStatusText GroupStatus { get; } = new(Localizer, RelayStatus, Events);

    public static GroupPlanText GroupPlan { get; } = new(Localizer, Events);

    public static SetupText Setup { get; } = new(Localizer);
}
