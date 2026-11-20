namespace Contactor.Tests.Web;

/// <summary>
/// The pages' colours: every one from a design token, so dark mode works on every page.
/// </summary>
public sealed class PwaTests
{
    [Fact]
    public void Should_TakeEveryPageColourFromTheTokens()
    {
        // A colour written into a page's CSS stays the same in dark mode; the tokens in app.css change. The only one
        // allowed is white text on the red "Delete" button, which is dark enough in both.
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web", "Components");
        var colours = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{3,8}\b");
        List<string> found = [];
        foreach (var file in Directory.GetFiles(folder, "*.razor.css"))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (colours.IsMatch(line) && !line.Contains("color: #FFFFFF;", StringComparison.Ordinal))
                {
                    found.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }
}
