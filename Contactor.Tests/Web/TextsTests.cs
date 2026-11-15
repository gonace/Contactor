using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
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
/// The UI's texts in their resource files (<c>Contactor.Web/Resources/</c>, one per area): every key the code uses has
/// an English text, every language has a text for every key with the same placeholders, and the helpers for texts
/// with a count or with markup inside.
/// </summary>
public sealed partial class TextsTests
{
    private static readonly string WebFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web"));

    // Every area's texts: a marker class in Contactor.Web.Resources, named after its area ("Common" for CommonTexts).
    private static IReadOnlyList<Type> Areas => [.. typeof(CommonTexts).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(CommonTexts).Namespace && t.Name.EndsWith("Texts", StringComparison.Ordinal))];

    [Fact]
    public void Should_HaveATextInEveryLanguage_ForEveryKey()
    {
        Assert.NotEmpty(Areas);
        var problems = new List<string>();
        foreach (var area in Areas)
        {
            var english = Texts(area, CultureInfo.InvariantCulture);
            Assert.NotNull(english);
            problems.AddRange(Check(english, null, area.Name));
            foreach (var language in LanguageCatalogue.Default.All.Skip(1))
            {
                problems.AddRange(Check(english, Texts(area, language.Culture) ?? new Dictionary<string, string>(), $"{area.Name} in {language.Code}"));
            }
        }

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
        };
        var translated = new Dictionary<string, string>
        {
            ["Common.PageTitle"] = "Contactor · {1}",
            ["Common.Braces"] = "{{0}} är ingen",
            ["Common.Relays_One"] = " ",
            ["Common.Gone"] = "Borta",
        };

        var problems = Check(english, translated, "Test");

        Assert.Equal(
            [
                "Test: Common.Save has no text",
                "Test: Common.PageTitle has placeholders 1 instead of 0",
                "Test: Common.Relays_One has no text",
                "Test: Common.Gone isn't in English",
            ],
            problems);
        Assert.Equal(["English: Common.Relays_One has no Common.Relays_Other"], Check(english, null, "English"));
        Assert.Equal(["English: Bad has no area"], Check(new Dictionary<string, string> { ["Bad"] = "x" }, null, "English"));
    }

    [Fact]
    public void Should_HaveAnEnglishText_ForEveryKeyTheCodeUses()
    {
        var english = Areas.ToDictionary(a => Area(a), a => Texts(a, CultureInfo.InvariantCulture)!);
        var areas = string.Join('|', english.Keys);
        var used = new Regex($"\"((?:{areas})\\.[A-Za-z0-9.]+)\"");
        var missing = new List<string>();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(WebFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in used.Matches(File.ReadAllText(file)))
            {
                count++;
                var key = match.Groups[1].Value;
                var texts = english[key[..key.IndexOf('.', StringComparison.Ordinal)]];
                var known = texts.ContainsKey(key) || (texts.ContainsKey(key + Plurals.One) && texts.ContainsKey(key + Plurals.Other));
                if (!known)
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(count > 20, $"Only {count} keys found: the search no longer finds them.");
        Assert.Empty(missing);
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
    public async Task Should_ReadTheCommonTexts_ThroughTheLocalizer()
    {
        await using var app = new TestWebApp();
        var common = app.Services.GetRequiredService<IStringLocalizer<CommonTexts>>();

        var title = common["Common.PageTitle", common["Common.Screen.Relays"]];

        Assert.False(title.ResourceNotFound);
        Assert.Equal("Relays · Contactor", title.Value);
        Assert.True(common["Common.NoSuchKey"].ResourceNotFound);
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
            if (!texts.TryGetValue(key, out var translated) || string.IsNullOrWhiteSpace(translated))
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

    // The area's texts in exactly this language, without falling back; null when the language has no file.
    private static Dictionary<string, string>? Texts(Type area, CultureInfo culture)
    {
        var set = new ResourceManager(area).GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set?.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    // "Common" for CommonTexts: the first part of each of its keys.
    private static string Area(Type area) => area.Name[..^"Texts".Length];

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
