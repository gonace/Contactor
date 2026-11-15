using System.Text;
using System.Text.RegularExpressions;

namespace Contactor.Web.Features.Updates;

/// <summary>
/// An update's log, as the update helper writes it (<c>updates/logs/update-&lt;id&gt;.log</c>), for "Show what happened"
/// and its download. The helper strips secrets before it writes a line; the app strips them again before it shows one,
/// by the same rules (install/contactor-updater.sh's <c>strip_secrets</c>), since anything in that folder could have
/// been written by someone else.
/// </summary>
public static partial class UpdateLog
{
    /// <summary>The most of a log shown: its end, where what went wrong is.</summary>
    public const int MaxBytes = 256 * 1024;

    public const string Hidden = "[hidden]";

    /// <summary>The longest line kept, as the helper keeps it; anything after it is cut, with "…".</summary>
    public const int MaxLineLength = 2000;

    /// <summary>
    /// The first cut, before secrets are hidden, which keeps a huge line cheap: it drops the word it cuts through, which
    /// could be the start of a secret. The helper cuts at the same length.
    /// </summary>
    public const int FirstCut = 8000;

    /// <summary>The log of update <paramref name="id"/> in <paramref name="logsFolder"/>, stripped; null when there is none.</summary>
    public static async Task<string?> ReadAsync(string logsFolder, string? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logsFolder);
        if (!HelperStatus.IsId(id))
        {
            return null;
        }

        var path = Path.Combine(logsFolder, $"update-{id}.log");
        try
        {
            var info = new FileInfo(path);
            // Only a plain file: the folder is shared with the helper, and a link could point anywhere.
            if (!info.Exists || info.LinkTarget is not null)
            {
                return null;
            }

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var skip = Math.Max(0, file.Length - MaxBytes);
            file.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken);
            if (skip > 0)
            {
                // From the first whole line.
                var newline = text.IndexOf('\n', StringComparison.Ordinal);
                text = newline >= 0 ? text[(newline + 1)..] : text;
            }

            return Strip(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with anything that looks like a secret replaced by <see cref="Hidden"/>: a Tailscale
    /// key, a token (<c>eyJ…</c>, as the tunnel's and sign-ins' are), "Bearer …" and "Basic …", the value of any
    /// name=value or name: value whose name has token, key, password, secret or auth in it, and a long run of letters
    /// and digits that looks random. Digests and ordinary words and paths stay.
    /// </summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Secrets are hidden before the line is cut, so none is cut in half and left showing. Every pattern runs
            // without backtracking.
            var line = lines[i];
            var cut = line.Length > FirstCut;
            if (cut)
            {
                line = line[..FirstCut];
                line = line[..(line.LastIndexOfAny([' ', '\t']) + 1)];
            }

            line = TailscaleKey().Replace(line, Hidden);
            line = Token().Replace(line, Hidden);
            line = Bearer().Replace(line, Hidden);
            line = Basic().Replace(line, Hidden);
            line = Named().Replace(line, m => m.Groups["name"].Value + (m.Groups["value"].Success ? Hidden : ""));
            line = LongRun().Replace(line, m => LooksRandom(m.Value) ? Hidden : m.Value);
            lines[i] = line.Length > MaxLineLength ? line[..MaxLineLength] + "…" : cut ? line + "…" : line;
        }

        return string.Join('\n', lines);
    }

    private static bool LooksRandom(string run)
    {
        return run.Any(char.IsAsciiDigit) && run.Any(char.IsAsciiLetterUpper) && run.Any(char.IsAsciiLetterLower);
    }

    [GeneratedRegex(@"tskey-[A-Za-z0-9-]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TailscaleKey();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_.-]*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Token();

    [GeneratedRegex(@"[Bb][Ee][Aa][Rr][Ee][Rr] [^ \t]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"[Bb][Aa][Ss][Ii][Cc] [^ \t]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Basic();

    [GeneratedRegex(@"(?<name>(?i:token|key|password|passwd|pwd|secret|auth)[A-Za-z0-9_.-]*[""']?[ \t]*[:=][ \t]*[""']?)(?<value>[^ \t""',;&]+)?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Named();

    [GeneratedRegex(@"[A-Za-z0-9+=_-]{24,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LongRun();
}
