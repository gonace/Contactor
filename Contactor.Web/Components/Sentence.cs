using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Contactor.Web.Components;

/// <summary>
/// A text with markup inside, e.g. a name in bold or a link: the text comes whole from the resource files, with
/// <c>{0}</c>, <c>{1}</c> and <c>{2}</c> where <see cref="Arg0"/>, <see cref="Arg1"/> and <see cref="Arg2"/> go, so a
/// language can put them where its word order needs them. The text itself is shown as text, never as HTML, and
/// <c>{{</c> and <c>}}</c> are a brace.
/// <code>&lt;Sentence Text="@Relays["Relays.InGroup"]"&gt;&lt;Arg0&gt;&lt;strong&gt;@group.Name&lt;/strong&gt;&lt;/Arg0&gt;&lt;/Sentence&gt;</code>
/// </summary>
public sealed class Sentence : ComponentBase
{
    /// <summary>The text, with its placeholders.</summary>
    [Parameter]
    [EditorRequired]
    public string Text { get; set; } = "";

    /// <summary>What goes at <c>{0}</c>.</summary>
    [Parameter]
    public RenderFragment? Arg0 { get; set; }

    /// <summary>What goes at <c>{1}</c>.</summary>
    [Parameter]
    public RenderFragment? Arg1 { get; set; }

    /// <summary>What goes at <c>{2}</c>.</summary>
    [Parameter]
    public RenderFragment? Arg2 { get; set; }

    /// <summary>
    /// The text in parts: a part is either words or a placeholder's number. A placeholder that isn't <c>{0}</c> to
    /// <c>{2}</c>, or a brace on its own, stays as the words it is.
    /// </summary>
    public static IReadOnlyList<(string? Words, int Arg)> Parts(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<(string? Words, int Arg)>();
        var words = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
            {
                words.Append(c);
                i++;
            }
            else if (c == '{' && i + 2 < text.Length && text[i + 1] is >= '0' and <= '2' && text[i + 2] == '}')
            {
                if (words.Length > 0)
                {
                    parts.Add((words.ToString(), -1));
                    words.Clear();
                }

                parts.Add((null, text[i + 1] - '0'));
                i += 2;
            }
            else
            {
                words.Append(c);
            }
        }

        if (words.Length > 0)
        {
            parts.Add((words.ToString(), -1));
        }

        return parts;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (words, arg) in Parts(Text))
        {
            if (words is not null)
            {
                builder.AddContent(0, words);
            }
            else
            {
                builder.AddContent(1, arg switch
                {
                    0 => Arg0,
                    1 => Arg1,
                    _ => Arg2,
                });
            }
        }
    }
}
