using System.Globalization;
using System.Resources;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Languages;

/// <summary>
/// A text made where no page is, e.g. by a background service or a check that also runs at start: its key in the
/// resource file (<see cref="Texts"/>) and its values. A page puts it in words in the app's language
/// (<see cref="In"/>); the logs and the start's messages take its English (<see cref="English"/>).
/// </summary>
public sealed record UiText(string Key, IReadOnlyList<string> Values)
{
    private static readonly ResourceManager ResourceFile = new(typeof(Texts));

    /// <summary>The text <paramref name="key"/>, with its values.</summary>
    public static UiText Of(string key, params string[] values) => new(key, values);

    /// <summary>The text in words, in the current language, from the localizer.</summary>
    public string In(IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts[Key, [.. Values]];
    }

    /// <summary>The text in English, the neutral resource file's, whatever the language: for the logs.</summary>
    public string English()
    {
        var text = ResourceFile.GetString(Key, CultureInfo.InvariantCulture) ?? Key;
        return string.Format(CultureInfo.InvariantCulture, text, [.. Values]);
    }

    public bool Equals(UiText? other)
    {
        return other is not null && Key == other.Key && Values.SequenceEqual(other.Values);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, Values.Count);
    }

    public override string ToString() => English();
}
