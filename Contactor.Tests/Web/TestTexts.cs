using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
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

    public static GroupStatusText GroupStatus { get; } = new(For<GroupsTexts>(), RelayStatus);

    public static GroupPlanText GroupPlan { get; } = new(For<GroupsTexts>());

    public static IStringLocalizer<T> For<T>() => new StringLocalizer<T>(Factory);
}
