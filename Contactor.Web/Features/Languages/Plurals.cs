using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Languages;

/// <summary>
/// Texts with a count, e.g. "1 relay" and "8 relays": one key per plural form, <c>Key_One</c> and <c>Key_Other</c>,
/// each with the count as <c>{0}</c>. Which form a count takes is the language's own rule.
/// </summary>
public static class Plurals
{
    public const string One = "_One";
    public const string Other = "_Other";

    /// <summary>The text of <paramref name="key"/> for <paramref name="count"/>, in the current language.</summary>
    public static string Plural(this IStringLocalizer localizer, string key, long count)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[key + Form(count), count];
    }

    /// <summary>
    /// The suffix of the form <paramref name="count"/> takes: English's rule, one for 1 and other for the rest. A
    /// language with other forms makes it depend on <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>, with a key per form.
    /// </summary>
    public static string Form(long count)
    {
        return count == 1 ? One : Other;
    }
}
