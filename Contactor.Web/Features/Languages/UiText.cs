using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Languages;

/// <summary>
/// A text made where no page is, e.g. by a background service or a check that also runs at start: its key in an area's
/// resource file (<paramref name="Area"/>, e.g. SetupTexts) and its values. A page puts it in words in the app's
/// language (<see cref="In"/>); the logs and the start's messages take its English (<see cref="English"/>).
/// </summary>
public sealed record UiText(Type Area, string Key, IReadOnlyList<string> Values)
{
    private static readonly ConcurrentDictionary<Type, ResourceManager> Resources = new();

    /// <summary>The text <paramref name="key"/> of <typeparamref name="TArea"/>'s resource file, with its values.</summary>
    public static UiText Of<TArea>(string key, params string[] values) => new(typeof(TArea), key, values);

    /// <summary>The text in words, in the current language, from the area's localizer.</summary>
    public string In(IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts[Key, [.. Values]];
    }

    /// <summary>The text in English, the neutral resource file's, whatever the language: for the logs.</summary>
    public string English()
    {
        var text = Resources.GetOrAdd(Area, area => new ResourceManager(area)).GetString(Key, CultureInfo.InvariantCulture) ?? Key;
        return string.Format(CultureInfo.InvariantCulture, text, [.. Values]);
    }

    public bool Equals(UiText? other)
    {
        return other is not null && Area == other.Area && Key == other.Key && Values.SequenceEqual(other.Values);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Area, Key, Values.Count);
    }

    public override string ToString() => English();
}
