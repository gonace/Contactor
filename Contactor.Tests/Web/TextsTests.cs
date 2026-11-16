using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Contactor.Web.Components;
using Contactor.Web.Features.Languages;
using Contactor.Web.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>
/// The UI's texts in their resource files (<c>Contactor.Web/Resources/</c>, one per language, sorted by key): every key
/// the code uses has an English text, every language has a text for every key with the same placeholders, and the
/// helpers for texts with a count or with markup inside.
/// </summary>
public sealed partial class TextsTests
{
    private static readonly string WebFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web"));

    private static readonly string ResourcesFolder = Path.Combine(WebFolder, "Resources");

    [Fact]
    public void Should_HaveATextInEveryLanguage_ForEveryKey()
    {
        var english = Read(CultureInfo.InvariantCulture);
        Assert.NotNull(english);
        Assert.NotEmpty(english);
        var problems = Check(english, null, "English");
        foreach (var language in LanguageCatalogue.Default.All.Skip(1))
        {
            problems.AddRange(Check(english, Read(language.Culture) ?? new Dictionary<string, string>(), language.Code));
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Should_HaveOneFile_PerLanguage()
    {
        var expected = LanguageCatalogue.Default.All.Skip(1).Select(l => $"Texts.{l.Code}.resx").Prepend("Texts.resx").Order(StringComparer.Ordinal);

        var files = Directory.EnumerateFiles(ResourcesFolder, "*.resx").Select(Path.GetFileName).Order(StringComparer.Ordinal);

        Assert.Equal(expected, files);
    }

    [Fact]
    public void Should_KeepEveryFile_SortedByKey()
    {
        var files = Directory.EnumerateFiles(ResourcesFolder, "*.resx").ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var keys = XDocument.Load(file).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToList();

            Assert.True(keys.Count > 0, $"{Path.GetFileName(file)} has no texts.");
            // Ordinal order: upper case before lower case, e.g. "Relays.RelayLabel" before "Relays.Relayed". The
            // failure names the first key out of order.
            var at = Enumerable.Range(1, keys.Count - 1).FirstOrDefault(i => string.CompareOrdinal(keys[i - 1], keys[i]) > 0);
            Assert.True(at == 0, at == 0 ? "" : $"{Path.GetFileName(file)} isn't sorted by key: \"{keys[at]}\" goes before \"{keys[at - 1]}\" (ordinal order, upper case before lower case).");
        }
    }

    [Fact]
    public void Should_KeepEveryFormat_ACustomPattern_InEveryLanguage()
    {
        // A key ending in "Format" holds a .NET date format, e.g. Relays.When.DateFormat's "d MMM". A one-letter
        // standard format ("d", "t") would follow the language's own long or short forms instead of the pattern.
        var formats = 0;
        var problems = new List<string>();
        foreach (var language in LanguageCatalogue.Default.All)
        {
            var texts = Read(language == LanguageCatalogue.Default.Fallback ? CultureInfo.InvariantCulture : language.Culture) ?? [];
            foreach (var (key, format) in texts.Where(t => t.Key.EndsWith("Format", StringComparison.Ordinal)))
            {
                formats++;
                if (format.Trim().Length <= 1)
                {
                    problems.Add($"{key} in {language.Code}: \"{format}\" is a standard format");
                    continue;
                }

                try
                {
                    _ = new DateTime(2026, 9, 3, 14, 2, 0).ToString(format, language.Culture);
                }
                catch (FormatException)
                {
                    problems.Add($"{key} in {language.Code}: \"{format}\" isn't a date format");
                }
            }
        }

        Assert.True(formats > 0, "No format found: the search no longer finds them.");
        Assert.Empty(problems);
    }

    [Fact]
    public void Should_FindAMissingText_AnEmptyOne_AndOtherPlaceholders()
    {
        var english = new Dictionary<string, string>
        {
            ["Common.Save"] = "Save",
            ["Common.PageTitle"] = "{0} · Contactor",
            ["Common.Braces"] = "{{0}} is no placeholder",
            ["Common.Relays_One"] = "{0} relay",
            ["Common.Separator"] = " ",
            ["Common.Between"] = " · ",
        };
        var translated = new Dictionary<string, string>
        {
            ["Common.PageTitle"] = "Contactor · {1}",
            ["Common.Braces"] = "{{0}} är ingen",
            ["Common.Relays_One"] = " ",
            ["Common.Separator"] = " ",
            ["Common.Between"] = "",
            ["Common.Gone"] = "Borta",
        };

        var problems = Check(english, translated, "Test");

        Assert.Equal(
            [
                "Test: Common.Save has no text",
                "Test: Common.PageTitle has placeholders 1 instead of 0",
                "Test: Common.Relays_One has no text",
                "Test: Common.Between has no text",
                "Test: Common.Gone isn't in English",
            ],
            problems);
        Assert.Equal(["English: Common.Relays_One has no Common.Relays_Other"], Check(english, null, "English"));
        Assert.Equal(["English: Bad has no area"], Check(new Dictionary<string, string> { ["Bad"] = "x" }, null, "English"));
    }

    [Fact]
    public void Should_HaveAnEnglishText_ForEveryKeyTheCodeUses()
    {
        var english = Read(CultureInfo.InvariantCulture)!;
        var files = Directory.EnumerateFiles(WebFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

        var (count, missing) = MissingKeys(files, english);

        Assert.True(count > 20, $"Only {count} keys found: the search no longer finds them.");
        Assert.Empty(missing);
    }

    [Fact]
    public void Should_FindAKeyWithoutAText_AlsoWithAnUnderscore()
    {
        var english = new Dictionary<string, string> { ["Common.Save"] = "Save", ["Common.Relays_One"] = "{0} relay", ["Common.Relays_Other"] = "{0} relays" };
        var code = """
            @Texts["Common.Save"] @Texts["Common.Missing_Key"] @Texts.Plural("Common.Relays", 2) @Texts["Common.Relays_Other"]
            "Other.Thing" "Common.Gone"
            /// <see cref="Common.Elsewhere"/> <param name="Common.Param">
            filename="Common.Filed"
            """;

        var (count, missing) = MissingKeys([("Page.razor", code)], english);

        Assert.Equal(6, count);
        Assert.Equal(["Page.razor: Common.Missing_Key", "Page.razor: Common.Gone", "Page.razor: Common.Filed"], missing);
    }

    [Fact]
    public void Should_SplitASentence_AtItsPlaceholders()
    {
        Assert.Equal(
            [("In ", -1), (null, 0), (", then ", -1), (null, 2), (".", -1)],
            Sentence.Parts("In {0}, then {2}."));
        Assert.Equal([(null, 1)], Sentence.Parts("{1}"));
        Assert.Equal([("{0} stays {3} and { and }", -1)], Sentence.Parts("{{0}} stays {3} and { and }"));
        Assert.Empty(Sentence.Parts(""));
    }

    [Fact]
    public async Task Should_PutMarkupIntoASentence_AndShowTheTextAsText()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        RenderFragment bold = builder =>
        {
            builder.OpenElement(0, "strong");
            builder.AddContent(1, "Fan heater");
            builder.CloseElement();
        };
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(Sentence.Text)] = "<b>{1}</b> is in {0}.",
            [nameof(Sentence.Arg0)] = bold,
            [nameof(Sentence.Arg1)] = (RenderFragment)(builder => builder.AddContent(0, "Heat")),
        });

        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Sentence>(parameters)).ToHtmlString());

        Assert.Equal("&lt;b&gt;Heat&lt;/b&gt; is in <strong>Fan heater</strong>.", html);
    }

    [Fact]
    public void Should_ChooseThePluralForm_ByTheCount()
    {
        var texts = new FixedTexts(new Dictionary<string, string>
        {
            ["Test.Relays_One"] = "{0} relay",
            ["Test.Relays_Other"] = "{0} relays",
        });

        Assert.Equal("1 relay", texts.Plural("Test.Relays", 1));
        Assert.Equal("0 relays", texts.Plural("Test.Relays", 0));
        Assert.Equal("8 relays", texts.Plural("Test.Relays", 8));
    }

    [Fact]
    public async Task Should_ReadTheTexts_ThroughTheLocalizer()
    {
        await using var app = new TestWebApp();
        var texts = app.Services.GetRequiredService<IStringLocalizer<Texts>>();

        var title = texts["Common.PageTitle", texts["Common.Screen.Relays"]];

        Assert.False(title.ResourceNotFound);
        Assert.Equal("Relays · Contactor", title.Value);
        Assert.True(texts["Common.NoSuchKey"].ResourceNotFound);
    }

    /// <summary>
    /// What is wrong with <paramref name="texts"/> against English: a key without a text, other placeholders than
    /// English's, or a key English doesn't have. Without <paramref name="texts"/>, English itself: each key in an area,
    /// and each "_One" with its "_Other".
    /// </summary>
    private static List<string> Check(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string>? texts, string name)
    {
        var problems = new List<string>();
        if (texts is null)
        {
            foreach (var key in english.Keys)
            {
                if (!key.Contains('.', StringComparison.Ordinal))
                {
                    problems.Add($"{name}: {key} has no area");
                }
                else if (key.EndsWith(Plurals.One, StringComparison.Ordinal) && !english.ContainsKey(key[..^Plurals.One.Length] + Plurals.Other))
                {
                    problems.Add($"{name}: {key} has no {key[..^Plurals.One.Length] + Plurals.Other}");
                }
            }

            return problems;
        }

        foreach (var (key, text) in english)
        {
            // Only a separator that is blank in English, e.g. Groups.Plan.Separator's " ", may be blank in a language.
            if (!texts.TryGetValue(key, out var translated) || translated.Length == 0 || (string.IsNullOrWhiteSpace(translated) && !string.IsNullOrWhiteSpace(text)))
            {
                problems.Add($"{name}: {key} has no text");
            }
            else if (Placeholders(translated) != Placeholders(text))
            {
                problems.Add($"{name}: {key} has placeholders {Placeholders(translated)} instead of {Placeholders(text)}");
            }
        }

        problems.AddRange(texts.Keys.Where(k => !english.ContainsKey(k)).Select(k => $"{name}: {k} isn't in English"));
        return problems;
    }

    // "0, 1": the placeholders a text has, each once, in order; an escaped brace ("{{") isn't one.
    private static string Placeholders(string text)
    {
        var plain = text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);
        return string.Join(", ", PlaceholderPattern().Matches(plain).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order());
    }

    /// <summary>
    /// The keys <paramref name="files"/> use that have no English text: every string literal that starts with an area,
    /// the first part of an English key, e.g. "Common.Save". A plural's key, as <see cref="Plurals.Plural"/> takes it,
    /// needs its "_One" and "_Other". A key put together in code (an interpolated string) isn't seen: write keys whole.
    /// </summary>
    private static (int Count, List<string> Missing) MissingKeys(IEnumerable<(string Name, string Text)> files, IReadOnlyDictionary<string, string> english)
    {
        var areas = english.Keys.Where(k => k.Contains('.', StringComparison.Ordinal)).Select(k => Regex.Escape(k[..k.IndexOf('.', StringComparison.Ordinal)])).Distinct();

        // Not a name in a doc comment, e.g. <see cref="Groups.GroupsHandler"/>.
        var used = new Regex($"(?<!\\b(?:cref|name)=)\"((?:{string.Join('|', areas)})\\.[A-Za-z0-9._]+)\"");
        var missing = new List<string>();
        var count = 0;
        foreach (var (name, text) in files)
        {
            foreach (Match match in used.Matches(text))
            {
                count++;
                var key = match.Groups[1].Value;
                if (!english.ContainsKey(key) && !(english.ContainsKey(key + Plurals.One) && english.ContainsKey(key + Plurals.Other)))
                {
                    missing.Add($"{name}: {key}");
                }
            }
        }

        return (count, missing);
    }

    // The texts in exactly this language, without falling back; null when the language has no file.
    private static Dictionary<string, string>? Read(CultureInfo culture)
    {
        var set = new ResourceManager(typeof(Texts)).GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set?.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    // Texts from a dictionary, formatted in the invariant culture.
    private sealed class FixedTexts : IStringLocalizer
    {
        private readonly IReadOnlyDictionary<string, string> _texts;

        public FixedTexts(IReadOnlyDictionary<string, string> texts)
        {
            _texts = texts;
        }

        public LocalizedString this[string name] => new(name, _texts.GetValueOrDefault(name, name), !_texts.ContainsKey(name));

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, this[name].Value, arguments), !_texts.ContainsKey(name));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => _texts.Select(t => new LocalizedString(t.Key, t.Value));
    }
}
