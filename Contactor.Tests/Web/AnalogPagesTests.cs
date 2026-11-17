using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>The analog inputs on the Relays screen and their own screen: the value, its unit, and why there is none.</summary>
public sealed partial class AnalogPagesTests
{
    private const string John = "john@example.com";

    // Four analog inputs, 10 bits for 0–5 V.
    private static readonly AnalogAbility FourInputs = new(4, 1023, 5m);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Analog 1 is a temperature, 0–5 V for −20 to 80 °C: 430 counts is 2.10 V, 22.0 °C. Analog 2 gives no reading,
    // analog 3 is at the top of the board's range, analog 4 is volts: 512 counts is 2.50 V.
    private static AnalogReadings Readings(int first = 430, int fourth = 512) => new([first, null, 1023, fourth]);

    [Fact]
    public async Task Should_ShowEachAnalogInputsValue_AfterTheInputs()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        board.SetAnalog(Readings());
        using var http = app.CreateClient();

        var html = await http.GetStringAsync("/", Cancel);

        Assert.True(html.IndexOf("href=\"input/1\"", StringComparison.Ordinal) < html.IndexOf("href=\"analog/1\"", StringComparison.Ordinal));
        Assert.Equal("ANALOG 1 Workshop 22.0 °C", CardText(html, "analog/1"));
        Assert.Equal("ANALOG 2 Analog 2 No reading: check the sensor – No value", CardText(html, "analog/2"));
        Assert.Equal("ANALOG 3 Analog 3 Above the board's range – No value", CardText(html, "analog/3"));
        Assert.Equal("ANALOG 4 Analog 4 2.50 V", CardText(html, "analog/4"));
        Assert.Contains("class=\"status problem\"", Card(html, "analog/2"), StringComparison.Ordinal);
        Assert.Contains("class=\"value none\"", Card(html, "analog/3"), StringComparison.Ordinal);

        // A screen reader hears "No value", not the dash.
        Assert.Matches("class=\"number\" aria-hidden=\"true\"[^>]*>(–|&#x2013;)</span><span class=\"visually-hidden\"[^>]*>No value</span>", Card(html, "analog/3"));
        Assert.DoesNotContain("No value", Card(html, "analog/1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowWaiting_BeforeAReading_AndADash_WithoutContact()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var http = app.CreateClient();

        var waiting = await http.GetStringAsync("/", Cancel);
        board.SetAnalog(Readings());
        board.SetConnected(false);
        var lost = await http.GetStringAsync("/", Cancel);

        Assert.Equal("ANALOG 1 Workshop Waiting for the board – No value", CardText(waiting, "analog/1"));
        Assert.Contains("unknown", Card(waiting, "analog/1")[..Card(waiting, "analog/1").IndexOf('>', StringComparison.Ordinal)], StringComparison.Ordinal);

        // The reading from before may be stale: no value, and the board's own line says why.
        Assert.Equal("ANALOG 1 Workshop – No value", CardText(lost, "analog/1"));
        Assert.Equal("ANALOG 2 Analog 2 – No value", CardText(lost, "analog/2"));
    }

    [Fact]
    public async Task Should_CountAnInactiveAnalogInput_AsAnInactiveInput()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            (await db.AnalogInputs.SingleAsync(i => i.Id == 2, Cancel)).IsActive = false;
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Show 1 inactive input", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"analog/2\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"analog/1\"", html, StringComparison.Ordinal);
    }

    // With several boards each board's section has its own analog inputs, after its inputs.
    [Fact]
    public async Task Should_ShowAnalogInputs_InTheirBoardsSection()
    {
        var barn = new FakeRelayBoard(relayCount: 4, inputCount: 2, analog: new AnalogAbility(1, 1023, 5m));
        await using var boards = await TwoBoards.StartAsync(barn);
        barn.SetAnalog(new AnalogReadings([1000]));
        int analogId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            analogId = await db.AnalogInputs.Where(i => i.BoardId == boards.BarnId).Select(i => i.Id).SingleAsync(Cancel);
        }

        using var http = boards.App.CreateClient();
        var html = await http.GetStringAsync("/", Cancel);

        var section = html[html.IndexOf($"id=\"board-{boards.BarnId}\"", StringComparison.Ordinal)..];
        Assert.Contains($"href=\"analog/{analogId}\"", section, StringComparison.Ordinal);
        Assert.Equal("ANALOG 1 Analog 1 4.89 V", CardText(html, $"analog/{analogId}"));
        Assert.Single(Regex.Matches(html, "href=\"analog/"));
    }

    // A reading moves by a count on most polls: the page redraws only when a value it shows changes.
    [Fact]
    public async Task Should_RedrawTheRelaysScreen_OnlyWhenAShownValueChanges()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.True(await RedrawsAsync(page, () => board.SetAnalog(Readings())));
        Assert.Contains("22.0", await page.TextAsync(), StringComparison.Ordinal);

        // 511 counts is 2.4976 V: still "2.50".
        Assert.False(await RedrawsAsync(page, () => board.SetAnalog(Readings(fourth: 511))));

        Assert.True(await RedrawsAsync(page, () => board.SetAnalog(Readings(first: 440, fourth: 511))));
        Assert.Contains("23.0", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_StopListeningForReadings_WhenThePagesClose()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var before = Listeners(registry);
        var relays = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        var analog = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(1));
        Assert.Equal(before + 2, Listeners(registry));

        relays.Close();
        analog.Close();
        board.SetAnalog(Readings());

        Assert.Equal(before, Listeners(registry));
        Assert.Empty(relays.Errors);
        Assert.Empty(analog.Errors);
    }

    [Fact]
    public async Task Should_ShowTheAnalogInputScreen_WithTheVoltageUnderTheValue()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        board.SetAnalog(Readings());
        using var http = app.CreateClient();

        var scaled = WebUtility.HtmlDecode(await http.GetStringAsync("/analog/1", Cancel));
        var volts = WebUtility.HtmlDecode(await http.GetStringAsync("/analog/4", Cancel));
        var noReading = WebUtility.HtmlDecode(await http.GetStringAsync("/analog/2", Cancel));
        var aboveRange = WebUtility.HtmlDecode(await http.GetStringAsync("/analog/3", Cancel));
        var missing = WebUtility.HtmlDecode(await http.GetStringAsync("/analog/9", Cancel));

        Assert.Contains("<title>Workshop · Contactor</title>", scaled, StringComparison.Ordinal);
        Assert.Contains("ANALOG 1 · NAME", scaled, StringComparison.Ordinal);
        Assert.Matches("<span class=\"number\"[^>]*>22.0</span>", scaled);
        Assert.Contains("The board reads 2.10 V.", scaled, StringComparison.Ordinal);
        Assert.Contains("Use the reading now (2.10 V)", scaled, StringComparison.Ordinal);
        Assert.Matches("aria-pressed=\"true\"[^>]*>°C</button>", scaled);
        Assert.Matches("id=\"value-1\"[^>]*value=\"-20\"|value=\"-20\"[^>]*id=\"value-1\"", scaled);

        // Volts until a unit is chosen: no scale to fill in, and no second line with the same voltage.
        Assert.Matches("<span class=\"number\"[^>]*>2.50</span>", volts);
        Assert.DoesNotContain("The board reads", volts, StringComparison.Ordinal);
        Assert.Contains("+ Show it as °C, %, bar …", volts, StringComparison.Ordinal);
        Assert.DoesNotContain("BOARD READS", volts, StringComparison.Ordinal);

        Assert.Contains("No reading: check the sensor", noReading, StringComparison.Ordinal);
        Assert.Contains("Above the board's range", aboveRange, StringComparison.Ordinal);
        Assert.Contains("The analog input doesn't exist.", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SaveTheNameUnitAndScale()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(4));

        await page.ClickAsync(PageRenderer.WithText("+ Show it as °C, %, bar …"));
        await page.ClickAsync(PageRenderer.WithText("bar"));
        await page.TypeAsync(PageRenderer.With("id", "name"), " Compressor ");
        await page.TypeAsync(PageRenderer.With("id", "volts-1"), "0,5");
        await page.TypeAsync(PageRenderer.With("id", "value-1"), "0");
        await page.TypeAsync(PageRenderer.With("id", "volts-2"), "4.5");
        await page.TypeAsync(PageRenderer.With("id", "value-2"), "10");
        await page.SubmitAsync(PageRenderer.With("id", "analog-form"));

        var navigation = Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal("http://localhost/", navigation.Location);
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var saved = await db.AnalogInputs.AsNoTracking().SingleAsync(i => i.Id == 4, Cancel);
        Assert.Equal(("Compressor", AnalogUnits.Bar, 0.5m, 0m, 4.5m, 10m), (saved.Name, saved.Unit, saved.Volts1, saved.Value1, saved.Volts2, saved.Value2));
    }

    [Theory]
    [InlineData("°C", "", "", "", "", "This unit needs a scale: fill in both points.")]
    [InlineData("°C", "0", "-20", "5", "", "Fill in both points of the scale, or none.")]
    [InlineData("°C", "abc", "-20", "5", "80", "Type the scale's points as numbers, e.g. 2.5.")]
    [InlineData("°C", "1.2.3", "-20", "5", "80", "Type the scale's points as numbers, e.g. 2.5.")]
    [InlineData("°C", "2", "-20", "2", "80", "The two points need different voltages.")]
    [InlineData("Other", "0", "0", "5", "100", "Enter the unit.")]
    [InlineData(null, "0", "0", "5", "100", "Choose a unit.")]
    public async Task Should_SayWhatIsWrong_AndSaveNothing(string? unit, string volts1, string value1, string volts2, string value2, string message)
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(4));

        await page.ClickAsync(PageRenderer.WithText("+ Show it as °C, %, bar …"));
        if (unit is not null)
        {
            await page.ClickAsync(PageRenderer.WithText(unit));
        }

        await page.TypeAsync(PageRenderer.With("id", "volts-1"), volts1);
        await page.TypeAsync(PageRenderer.With("id", "value-1"), value1);
        await page.TypeAsync(PageRenderer.With("id", "volts-2"), volts2);
        await page.TypeAsync(PageRenderer.With("id", "value-2"), value2);
        await page.SubmitAsync(PageRenderer.With("id", "analog-form"));

        Assert.Contains(message, await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var row = await db.AnalogInputs.AsNoTracking().SingleAsync(i => i.Id == 4, Cancel);
        Assert.Equal((AnalogUnits.None, false), (row.Unit, row.HasScale));
    }

    // "Use the reading now": the first empty point takes the voltage and its value is typed next; with both points
    // set, the one nearer the reading takes it.
    [Fact]
    public async Task Should_UseTheReadingNow_InAPoint()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        board.SetAnalog(Readings());
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(4));
        await page.ClickAsync(PageRenderer.WithText("+ Show it as °C, %, bar …"));

        await page.ClickAsync(PageRenderer.WithText("Use the reading now (2.50 V)"));
        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "volts-1")(a) && PageRenderer.With("value", "2.50")(a)));
        Assert.Equal("value-1", page.Focused[^1]);

        await page.TypeAsync(PageRenderer.With("id", "value-1"), "21");
        await page.ClickAsync(PageRenderer.WithText("Use the reading now (2.50 V)"));
        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "volts-2")(a) && PageRenderer.With("value", "2.50")(a)));
        Assert.Equal("value-2", page.Focused[^1]);

        // Both set: 4.0 V is nearer 2.50 V than 0.5 V is, so the second point takes the reading, and loses its value.
        await page.TypeAsync(PageRenderer.With("id", "volts-1"), "0.5");
        await page.TypeAsync(PageRenderer.With("id", "volts-2"), "4");
        await page.TypeAsync(PageRenderer.With("id", "value-2"), "30");
        await page.ClickAsync(PageRenderer.WithText("Use the reading now (2.50 V)"));
        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "volts-2")(a) && PageRenderer.With("value", "2.50")(a)));
        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "value-2")(a) && PageRenderer.With("value", "")(a)));
        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "value-1")(a) && PageRenderer.With("value", "21")(a)));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_OfferNoReading_WithoutAValue()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(4));
        await page.ClickAsync(PageRenderer.WithText("+ Show it as °C, %, bar …"));

        Assert.True(await page.HasAsync(a => PageRenderer.WithText("Use the reading now")(a) && a.ContainsKey("disabled")));
        Assert.Contains("Waiting for the board", await page.TextAsync(), StringComparison.Ordinal);
    }

    // The page stays when the link goes to another analog input: a message from saving the first isn't the second's.
    [Fact]
    public async Task Should_ClearTheSaveMessage_ForAnotherAnalogInput()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(1));
        await page.TypeAsync(PageRenderer.With("id", "volts-1"), "abc");
        await page.SubmitAsync(PageRenderer.With("id", "analog-form"));
        Assert.Contains("Type the scale's points as numbers", await page.TextAsync(), StringComparison.Ordinal);

        await page.SetParametersAsync(Id(4));

        Assert.True(await page.HasAsync(a => PageRenderer.With("id", "name")(a) && PageRenderer.With("value", "Analog 4")(a)));
        Assert.DoesNotContain("Type the scale's points as numbers", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // The analog page shows the voltage too: a reading that changes only the voltage shown redraws it, noise doesn't.
    [Fact]
    public async Task Should_RedrawTheAnalogInputScreen_OnlyWhenAShownTextChanges()
    {
        var board = Board();
        await using var app = await AppAsync(board);
        board.SetAnalog(Readings());
        using var page = await PageRenderer.StartAsync<AnalogInputDetail>(app.Services, John, "John", Id(1));
        Assert.Contains("The board reads 2.10 V.", await page.TextAsync(), StringComparison.Ordinal);

        // Only another input's reading changes: nothing shown here.
        Assert.False(await RedrawsAsync(page, () => board.SetAnalog(Readings(fourth: 300))));

        Assert.True(await RedrawsAsync(page, () => board.SetAnalog(Readings(first: 431, fourth: 300))));
        Assert.Contains("The board reads 2.11 V.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Contains("22.1", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // Whether a new reading from the board redraws the page. The test's change sends the reading on the page's own
    // thread, so the page handles it at once, and nothing else the app does meanwhile can draw the page before the count
    // is read: e.g. an event the app logs at start, which redraws the page too.
    private static Task<bool> RedrawsAsync(PageRenderer page, Action change)
    {
        return page.Dispatcher.InvokeAsync(() =>
        {
            var renders = page.PageRenders;
            change();
            return page.PageRenders != renders;
        });
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("2.08", true, "2.08")]
    [InlineData("2,08", true, "2.08")]
    [InlineData("-20", true, "-20")]
    [InlineData("−20", true, "-20")]
    [InlineData(" 5 ", true, "5")]
    [InlineData("1,000.5", false, null)]
    [InlineData("1e3", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("- 5", false, null)]
    [InlineData("123456789012345678901234567890123", false, null)]
    public void Should_ReadATypedNumber(string text, bool ok, string? expected)
    {
        Assert.Equal(ok, AnalogNumbers.TryParse(text, out var value));
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("0", "", 1)]
    [InlineData("x", "5", 0)]
    [InlineData("0", "5", 0)]
    [InlineData("0", "4.9", 1)]
    [InlineData("2", "5", 0)]
    public void Should_PutANewReading_InTheEmptyOrNearerPoint(string volts1, string volts2, int point)
    {
        Assert.Equal(point, AnalogNumbers.Nearer(volts1, volts2, 2.5m));
    }

    private static FakeRelayBoard Board()
    {
        return new FakeRelayBoard(relayCount: 4, inputCount: 1, analog: FourInputs);
    }

    // The app with the board: analog 1 named Workshop, 0–5 V for −20 to 80 °C.
    private static async Task<TestWebApp> AppAsync(FakeRelayBoard board)
    {
        var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithBoard(board));
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var workshop = await db.AnalogInputs.SingleAsync(i => i.Id == 1, Cancel);
        (workshop.Name, workshop.Unit, workshop.Volts1, workshop.Value1, workshop.Volts2, workshop.Value2) = ("Workshop", AnalogUnits.Celsius, 0m, -20m, 5m, 80m);
        await db.SaveChangesAsync(Cancel);
        return app;
    }

    private static Dictionary<string, object?> Id(int id)
    {
        return new() { [nameof(AnalogInputDetail.Id)] = id };
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }

    // How many handlers listen for the boards' readings.
    private static int Listeners(BoardRegistry registry)
    {
        var field = typeof(BoardRegistry).GetField(nameof(BoardRegistry.AnalogChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (field!.GetValue(registry) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    // One card's markup: its <div class="card">, to the </div> that closes it.
    private static string Card(string html, string href)
    {
        var link = html.IndexOf($"href=\"{href}\"", StringComparison.Ordinal);
        Assert.True(link >= 0, $"No card for {href}.");
        var start = html.LastIndexOf("<div class=\"card", link, StringComparison.Ordinal);
        var depth = 0;
        foreach (Match tag in DivTags().Matches(html, start))
        {
            depth += tag.Value == "</div>" ? -1 : 1;
            if (depth == 0)
            {
                return html[start..(tag.Index + tag.Length)];
            }
        }

        throw new InvalidOperationException($"The card for {href} doesn't end.");
    }

    // One card's text, without its markup, its parts one space apart.
    private static string CardText(string html, string href)
    {
        var text = WebUtility.HtmlDecode(Tags().Replace(Card(html, href), " "));
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex("<div[\\s>]|</div>")]
    private static partial Regex DivTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Spaces();
}
