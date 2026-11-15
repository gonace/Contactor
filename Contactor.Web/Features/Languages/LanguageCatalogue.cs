using System.Globalization;

namespace Contactor.Web.Features.Languages;

/// <summary>A language the app's texts are in.</summary>
/// <param name="Code">Its culture's name, e.g. <c>en</c>; the resource files of its texts end in it (<c>*.sv.resx</c>).</param>
/// <param name="Name">Its name in itself, as the choice shows it, e.g. "English" or "Svenska".</param>
public sealed record Language(string Code, string Name)
{
    /// <summary>The culture its texts, dates and numbers are read in.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
}

/// <summary>
/// The languages the app has texts in: the one place a new language is added, next to its resource files. The first is
/// the default, the language of the neutral resource files.
/// </summary>
public sealed class LanguageCatalogue
{
    public static readonly Language English = new("en", "English");

    public static readonly Language Swedish = new("sv", "Svenska");

    /// <summary>The app's languages.</summary>
    public static readonly LanguageCatalogue Default = new([English, Swedish]);

    public LanguageCatalogue(IReadOnlyList<Language> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (all.Count == 0)
        {
            throw new ArgumentException("A catalogue needs at least one language.", nameof(all));
        }

        if (all.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count)
        {
            throw new ArgumentException("A language is in the catalogue twice.", nameof(all));
        }

        All = all;
    }

    /// <summary>Every language, the default first.</summary>
    public IReadOnlyList<Language> All { get; }

    /// <summary>The language of the neutral resource files, used until another is chosen.</summary>
    public Language Fallback => All[0];

    /// <summary>Whether there is anything to choose: the choices show only with two or more languages.</summary>
    public bool HasChoice => All.Count > 1;

    /// <summary>The language with this code, or null.</summary>
    public Language? Find(string? code)
    {
        return All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
    }
}
