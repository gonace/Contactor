using System.Globalization;
using System.Runtime.CompilerServices;

namespace Contactor.Tests;

/// <summary>
/// Every test runs in English, whatever the computer's own language: the texts and the dates and numbers in them are
/// read in the current culture, as in the app, where every request has the app's language.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }
}
