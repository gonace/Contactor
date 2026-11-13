using System.Text.RegularExpressions;

namespace Contactor.Tests.Web;

/// <summary>Every page follows the app's clock (TimeProvider), not the machine's, so a fake clock can move it.</summary>
public sealed partial class PageClockTests
{
    [Fact]
    public void Should_TakeEveryPageTimerAndTimeFromTheTimeProvider()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Contactor.Web", "Components");
        List<string> found = [];
        foreach (var file in Directory.GetFiles(folder, "*.razor*").Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal)))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (MachineClock().IsMatch(line))
                {
                    found.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("_tick = new Timer(_ => Refresh(), null, 1000, 1000);", true)]
    [InlineData("private System.Threading.Timer? _tick;", true)]
    [InlineData("private System.Timers.Timer? _tick;", true)]
    [InlineData("using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));", true)]
    [InlineData("Timer t = new(_ => Refresh());", true)]
    [InlineData("await Task.Delay(1000);", true)]
    [InlineData("var now = DateTime.UtcNow;", true)]
    [InlineData("_tick = Time.CreateTimer(_ => Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));", false)]
    [InlineData("await Task.Delay(2000, _cts.Token);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)));", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(1), Time);", false)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(1), Time, _cts.Token);", false)]
    [InlineData("private ITimer? _tick;", false)]
    public void Should_FindTheMachinesClock(string line, bool found)
    {
        Assert.Equal(found, MachineClock().IsMatch(line));
    }

    // A timer or a time of the machine's own: Timer, PeriodicTimer, a target-typed "Timer x = new(", DateTime.Now or
    // UtcNow, or a Task.Delay that doesn't pass the page's TimeProvider (named Time, or a TimeProvider) on its line.
    [GeneratedRegex(@"\bnew\s+(System\.(Threading|Timers)\.)?Timer\s*\(|System\.(Threading|Timers)\.Timer\b|\bPeriodicTimer\b|\bTimer\??\s+\w+\s*=\s*new\s*\(|\bDateTime(Offset)?\.(Utc)?Now\b|\bTask\.Delay\((?![^;]*\b(Time|TimeProvider)\b)")]
    private static partial Regex MachineClock();
}
