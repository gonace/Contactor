using System.Text;
using System.Text.RegularExpressions;

namespace Contactor.Web.Features.Updates;

/// <summary>
/// Reads a release's notes, which are markdown, into plain lines for "What's new". Only the list items are kept, without
/// the markdown's own characters. Bold (<c>**…**</c>) is the one markup kept: each kept <c>**</c> has a partner, and
/// <see cref="Parts"/> splits a line on them. The page writes every part as text, so no HTML in the notes ever reaches it.
/// </summary>
public static partial class ReleaseNotes
{
    /// <summary>The most changes kept for one release.</summary>
    public const int MaxChanges = 30;

    /// <summary>The longest change. A longer one ends at the last sentence or word that fits.</summary>
    public const int MaxChangeLength = 600;

    /// <summary>The most text kept for one release. The changes after it are left out whole.</summary>
    public const int MaxLength = 4000;

    // A line longer than this is cut before it is read, so a huge line costs no more than this.
    private const int MaxLineLength = 4096;

    private const string Bold = "**";

    /// <summary>
    /// The changes in a release's notes: each list item ("* " or "- "), as GitHub writes them ("* Title by @someone in
    /// https://…"), without who made it and the link. Headings, comments, code blocks, tables, "Full Changelog" and
    /// everything from the Images section on are left out. Links keep only their text, and code its characters.
    /// </summary>
    public static IReadOnlyList<string> Changes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return [];
        }

        var changes = new List<string>();
        var length = 0;
        var inComment = false;
        var fence = "";
        // CRLF as GitHub sends them, LF, or CR alone.
        foreach (var raw in notes.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            var line = (raw.Length > MaxLineLength ? raw[..MaxLineLength] : raw).Trim();

            // An unclosed comment or code block hides the rest of the notes.
            if (inComment)
            {
                var end = line.IndexOf("-->", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                inComment = false;
                line = line[(end + 3)..].Trim();
            }

            // A code block ends on a fence of its own character, at least as long as the one that opened it, with nothing
            // after it: "```js" inside a ``` block is a line of the block.
            if (fence.Length > 0)
            {
                if (line.StartsWith(fence, StringComparison.Ordinal) && line.AsSpan().TrimStart(fence[0]).IsEmpty)
                {
                    fence = "";
                }

                continue;
            }

            // The Release workflow's Images section at the end (Issue #314): a table and docker pull lines.
            if (line.Equals("<!-- images -->", StringComparison.OrdinalIgnoreCase) || line.Equals("## Images", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = new string(line[0], line.Length - line.TrimStart(line[0]).Length);
                continue;
            }

            line = WithoutComments(line, ref inComment);
            if (!(line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)))
            {
                continue;
            }

            var change = Change(line[2..]);
            if (change is null)
            {
                continue;
            }

            if (length + change.Length > MaxLength)
            {
                break;
            }

            changes.Add(change);
            length += change.Length;
            if (changes.Count == MaxChanges)
            {
                break;
            }
        }

        return changes;
    }

    /// <summary>
    /// A change split into its plain and bold parts, in order. A <c>**</c> without a partner, e.g. in a line saved by an
    /// older version, is left out, and so is a bold part with nothing to show.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Bold)> Parts(string change)
    {
        ArgumentNullException.ThrowIfNull(change);
        // A line saved by an older version can still hold a link's markdown or a ***.
        var pieces = Stars().Replace(Link().Replace(change, "$1"), Bold).Split(Bold);
        // An even number of pieces means an odd number of **: the last one has no partner, so what follows it is plain.
        var lastBold = pieces.Length % 2 == 0 ? pieces.Length - 2 : pieces.Length - 1;
        var parts = new List<(string Text, bool Bold)>();
        for (var i = 0; i < pieces.Length; i++)
        {
            var bold = i % 2 == 1 && i <= lastBold && !string.IsNullOrWhiteSpace(pieces[i]);
            if (pieces[i].Length == 0)
            {
                continue;
            }

            if (!bold && parts.Count > 0 && !parts[^1].Bold)
            {
                parts[^1] = (parts[^1].Text + pieces[i], false);
            }
            else
            {
                parts.Add((pieces[i], bold));
            }
        }

        return parts;
    }

    // One list item's text as it is kept, or null when nothing of it is left.
    private static string? Change(string item)
    {
        var change = ByAndLink().Replace(item, "").Trim();
        if (change.Length == 0 || change.StartsWith('@'))
        {
            return null;
        }

        change = Stars().Replace(Code().Replace(WithoutLinks(change), "$1"), Bold);
        change = Shortened(change);
        if (change is null)
        {
            return null;
        }

        change = Spaces().Replace(Balanced(change), " ").Trim();
        return change.Any(char.IsLetterOrDigit) ? change : null;
    }

    // Each link as its text, also a link in a link's text ([[a](u)](v) is "a"), so Parts, which unwraps links again for
    // lines saved by an older version, finds none left. Each pass makes the line shorter, so it ends.
    private static string WithoutLinks(string change)
    {
        while (true)
        {
            var text = Link().Replace(change, "$1");
            if (text.Length == change.Length)
            {
                return text;
            }

            change = text;
        }
    }

    // At most MaxChangeLength: up to the last full sentence that fits, when it ends in the last third, else up to the
    // last word that fits and "…", so an early sentence end doesn't leave out most of the change. Null when not even one
    // word fits.
    private static string? Shortened(string change)
    {
        if (change.Length <= MaxChangeLength)
        {
            return change;
        }

        var sentence = change.LastIndexOf(". ", MaxChangeLength - 1, StringComparison.Ordinal);
        if (sentence + 1 >= MaxChangeLength - MaxChangeLength / 3)
        {
            return change[..(sentence + 1)];
        }

        var word = change.LastIndexOf(' ', MaxChangeLength - 1);
        return word > 0 ? $"{change[..word].TrimEnd()}…" : null;
    }

    // Keeps the ** that have a partner and something between them; leaves out the others.
    private static string Balanced(string change)
    {
        var pieces = change.Split(Bold);
        if (pieces.Length == 1)
        {
            return change;
        }

        var lastBold = pieces.Length % 2 == 0 ? pieces.Length - 2 : pieces.Length - 1;
        var text = new StringBuilder(change.Length);
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i % 2 == 1 && i <= lastBold && !string.IsNullOrWhiteSpace(pieces[i]))
            {
                text.Append(Bold).Append(pieces[i]).Append(Bold);
            }
            else
            {
                text.Append(pieces[i]);
            }
        }

        return text.ToString();
    }

    // The line without its <!-- … --> comments. A comment that isn't closed on the line hides the rest of the notes.
    private static string WithoutComments(string line, ref bool inComment)
    {
        var start = line.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        var text = new StringBuilder(line.Length);
        var from = 0;
        while (start >= 0)
        {
            text.Append(line, from, start - from);
            var end = line.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                inComment = true;
                return text.ToString().Trim();
            }

            from = end + 3;
            start = line.IndexOf("<!--", from, StringComparison.Ordinal);
        }

        return text.Append(line, from, line.Length - from).ToString().Trim();
    }

    // The regular expressions run without backtracking, so their time grows only with the line's length, whatever it holds.

    // " by @someone in https://github.com/…/pull/12", or a bare " in https://…" at the end.
    [GeneratedRegex(@"(\s+by\s+@[\w-]+(\[bot\])?)?(\s+in\s+https?://\S+)?\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ByAndLink();

    // [text](url) and ![text](url): the text. The url may hold balanced parentheses, one level deep, and be followed by
    // a title: [text](url "title"), 'title' or (title).
    [GeneratedRegex(@"!?\[([^\[\]]*)\]\((?:[^()\s]|\([^()\s]*\))*(?:\s+(?:""[^""]*""|'[^']*'|\([^()]*\)))?\s*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Link();

    // Three or more *, e.g. ***bold and italic***: one ** for bold, never a * left over.
    [GeneratedRegex(@"\*{3,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Stars();

    // Two or more spaces, e.g. where a comment or a ** was: one, as the page would show them anyway.
    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Spaces();

    // `code`: the code.
    [GeneratedRegex(@"`([^`]*)`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Code();
}
