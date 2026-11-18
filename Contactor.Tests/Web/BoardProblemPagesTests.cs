using System.Globalization;
using System.Net;
using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Web;

/// <summary>
/// Why the app can't connect to a board, on every screen that says so: the setup guide's and "Add a board"'s step
/// "Where is the board?" (against the simulators), Settings with one board and each board's page, the boards' rows on
/// Settings and the desktop's side menu, and the Relays page's red box. A board that doesn't answer at all keeps its
/// texts.
/// </summary>
public sealed class BoardProblemPagesTests
{
    private const string John = TwoBoards.John;

    private static readonly ConnectionProblem WrongModel = new(ConnectionProblemKind.WrongModel, DevantechModels.Eth008, DevantechModels.Eth484);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Each kind, and none: the long text where the general one was, and the short one in a board's row.
    public static TheoryData<ConnectionProblem?, string, string> Problems { get; } = new()
    {
        { WrongModel, "The board at this address is an ETH484-B, not an ETH008-B.", "Wrong model" },
        { new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008), "The board at this address is another model, not an ETH008-B.", "Wrong model" },
        { new ConnectionProblem(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "The board asks for a password.", "Needs a password" },
        { new ConnectionProblem(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "The board asks for a user name and password.", "Needs a login" },
        { new ConnectionProblem(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "The password is wrong.", "Wrong password" },
        { new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), "The user name or password is wrong.", "Wrong login" },
        { new ConnectionProblem(ConnectionProblemKind.FirmwareTooOld, DevantechModels.Eth008, MinFirmware: "1.7.0"), "The board's firmware is too old. Update it to 1.7.0 or later on the board's own web page.", "Old firmware" },
        { new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4), "The board at this address has 4 relays, not 2.", "Wrong model" },
        { new ConnectionProblem(ConnectionProblemKind.CoverProfile, ShellyModels.TwoRelays), "The board is set up to drive a cover, not relays. Set its profile to \"switch\" on its own web page, then check again.", "Set up for a cover" },
        { new ConnectionProblem(ConnectionProblemKind.TooManyTries, DevantechModels.Eth008), "Too many wrong passwords. Check the password, then wait a few minutes: Contactor tries again by itself.", "Too many tries" },
        { null, "No contact with the relay board at", "No contact yet" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public async Task Should_SayWhy_OnSettings_WithOneBoard(ConnectionProblem? problem, string longText, string shortText)
    {
        var board = new FakeRelayBoard(connected: false);
        board.SetProblem(problem);
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithBoard(board));
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains(longText, html, StringComparison.Ordinal);
        if (problem is not null)
        {
            // The desktop's side menu, on every screen, has the short one.
            Assert.DoesNotContain("No contact with the relay board at", html, StringComparison.Ordinal);
            Assert.Matches($"<span class=\"no-contact\"[^>]*>{shortText}</span>", html);
        }

        // Another model the app knows is added again: the model can't be changed here.
        Assert.Equal(
            problem is { Kind: ConnectionProblemKind.WrongModel, Found: not null, FoundRelayCount: null },
            html.Contains("Add it again as an ETH484-B with 'Add a board', then remove this one.", StringComparison.Ordinal));
        Assert.Equal(
            problem is { FoundRelayCount: 4 },
            html.Contains("Add it again with 'Add a board' and choose the one with 4 relays, then remove this one.", StringComparison.Ordinal));
    }

    // The red box keeps its heading; the line under it says why, and where to go. The relays wait for the board.
    [Theory]
    [MemberData(nameof(Problems))]
    public async Task Should_SayWhy_OnRelays(ConnectionProblem? problem, string longText, string shortText)
    {
        _ = shortText;
        foreach (var answered in new[] { false, true })
        {
            var board = answered ? FakeRelayBoard.LostContact() : new FakeRelayBoard(connected: false);
            board.SetProblem(problem);
            await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithBoard(board));
            using var http = app.CreateClient();

            var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

            Assert.Contains(answered ? "No contact with the relay board" : "No contact with the relay board yet", html, StringComparison.Ordinal);
            if (problem is null)
            {
                Assert.Contains(answered ? "The states below may be wrong." : "Waiting for its first answer.", html, StringComparison.Ordinal);
                Assert.DoesNotContain("See Settings.", html, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"{longText} See Settings.", html, StringComparison.Ordinal);
                Assert.DoesNotContain("Waiting for its first answer.", html, StringComparison.Ordinal);
                Assert.DoesNotContain("The states below may be wrong.", html, StringComparison.Ordinal);
            }

            if (!answered)
            {
                Assert.Contains("Waiting for the board", html, StringComparison.Ordinal);
            }
        }
    }

    // With several boards: the short reason in the board's row on Settings and in the desktop's side menu, the long one
    // on the board's own page. The chips on a phone are as before.
    [Theory]
    [MemberData(nameof(Problems))]
    public async Task Should_SayWhy_InTheBoardsRows_AndOnItsPage(ConnectionProblem? problem, string longText, string shortText)
    {
        var barn = new FakeRelayBoard(relayCount: 4, connected: false, inputCount: 2);
        barn.SetProblem(problem);
        await using var boards = await TwoBoards.StartAsync(barn);
        using var http = boards.App.CreateClient();

        var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/board/{boards.BarnId}", Cancel));
        var relays = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Matches($"(?s)href=\"board/{boards.BarnId}\" class=\"board-row lost\".*?<span class=\"board-status\"[^>]*><span class=\"dot\"[^>]*></span>{shortText}</span>", settings);
        Assert.Contains(longText, page, StringComparison.Ordinal);
        var side = problem is null ? "No contact" : shortText;
        Assert.Matches($"(?s)class=\"side-board[^\"]* lost\".*?<span class=\"no-contact\"[^>]*>{side}</span>", relays);
        if (problem is not null)
        {
            // The side menu only: not the phone's chips.
            Assert.DoesNotContain("No contact yet", settings, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(relays, shortText));
        }
    }

    // A board's problem, and its end, redraws the filter: the side menu has the short reason.
    [Fact]
    public void Should_RedrawTheFilter_ForANewProblem()
    {
        var board = new FakeRelayBoard(connected: false);
        var shown = new ShownBoardStatesProbe(board);

        board.SetProblem(WrongModel);
        Assert.True(shown.Changed());
        board.SetProblem(WrongModel);
        Assert.False(shown.Changed());
        board.SetProblem(new ConnectionProblem(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008));
        Assert.True(shown.Changed());
        board.SetProblem(null);
        Assert.True(shown.Changed());
    }

    // "Add a board": another model at the address says so, and "Choose model" goes back to the model step.
    [Fact]
    public async Task Should_SayItIsAnotherModel_AndGoBackToTheModel_WhenAddingABoard()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Devantech", "devantech-eth008");
        await page.ClickAsync(Button("Next"));

        await AtAsync(page, simulator.Port);
        await page.ClickAsync(Button("Next"));

        var text = await page.TextAsync();
        Assert.Contains("The board at this address is an ETH484-B, not an ETH008-B.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No answer from", text, StringComparison.Ordinal);
        Assert.False(await page.HasAsync(Button("Check again")));
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);

        await page.ClickAsync(Button("Choose model"));

        Assert.Contains("STEP 1 OF 3", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // A board that asks for a password: "no password" is unticked and the cursor goes to Password, once for each new
    // reason; the right password goes on.
    [Fact]
    public async Task Should_AskForThePassword_AndPutTheCursorThere_OnceForEachReason()
    {
        await using var simulator = Started(DevantechModels.Eth008, "apple");
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Devantech", "devantech-eth008");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "password")));

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board asks for a password.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.With("id", "password")));
        Assert.Equal(["password"], page.Focused.Where(id => id is "password" or "username"));

        await page.TypeAsync(PageRenderer.With("id", "password"), "pear");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);
        await BoardTestRig.WaitUntilAsync(() => page.Focused.Count(id => id is "password") == 2);

        // The same reason again moves nothing: the person may be typing.
        await page.ClickAsync(Button("Check again"));
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(2, page.Focused.Count(id => id is "password" or "username"));

        await page.TypeAsync(PageRenderer.With("id", "password"), "apple");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // A Shelly added in the app: its password, then too many wrong ones, which says to check it and wait, then the right
    // one goes on, and the app switches its relays.
    [Fact]
    public async Task Should_AddAShelly_AndSayToWait_AfterTooManyWrongPasswords()
    {
        await using var simulator = new ShellySimulator(ShellyModels.TwoRelays, "apple", NullLogger.Instance);
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-2");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));
        Assert.Contains("The board asks for a password.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "username")));

        await page.TypeAsync(PageRenderer.With("id", "password"), "pear");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);

        // The device's brute-force protection: another password now gets 429.
        simulator.Throttle = true;
        await page.TypeAsync(PageRenderer.With("id", "password"), "plum");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Too many wrong passwords. Check the password, then wait a few minutes: Contactor tries again by itself.", await page.TextAsync(), StringComparison.Ordinal);

        simulator.Throttle = false;
        await page.TypeAsync(PageRenderer.With("id", "password"), "apple");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);

        var shelly = boards.App.Services.GetRequiredService<BoardRegistry>().All[^1];
        Assert.Equal(ChannelMask.Of(2), await shelly.TurnOnAsync(ChannelMask.Of(2), Cancel));
        Assert.Equal((byte)0b10, simulator.Relays);
    }

    // A Shelly with another number of relays: the counts, not the models, and "Choose model" back to the entries.
    [Fact]
    public async Task Should_SayTheRelayCount_OfAShellyWithAnotherNumber()
    {
        await using var simulator = new ShellySimulator(ShellyModels.FourRelays);
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-2");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));
        Assert.Contains("The board at this address has 4 relays, not 2.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(Button("Choose model")));
        Assert.Empty(page.Errors);
    }

    // A Shelly in the cover profile: no model fits, so the step says to set its profile and offers "Check again".
    [Fact]
    public async Task Should_SayToSetTheProfile_OfAShellyThatDrivesACover()
    {
        await using var simulator = new ShellySimulator(ShellyModels.TwoRelays) { Profile = "cover" };
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-2");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board is set up to drive a cover, not relays. Set its profile to \"switch\" on its own web page, then check again.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(Button("Choose model")));
        Assert.True(await page.HasAsync(Button("Check again")));

        simulator.Profile = "switch";
        await page.ClickAsync(Button("Check again"));
        await BoardTestRig.WaitUntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Board online", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // A Shelly with firmware before 1.7.0: the step says to update it.
    [Fact]
    public async Task Should_SayToUpdateTheFirmware_OfAnOldShelly()
    {
        await using var simulator = new ShellySimulator(ShellyModels.OneRelay) { Firmware = "1.6.2" };
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-1");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board's firmware is too old. Update it to 1.7.0 or later on the board's own web page.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(Button("Check again")));
        Assert.DoesNotContain(page.Focused, id => id is "password" or "username");
        Assert.Empty(page.Errors);
    }

    // A board with a user name: the cursor goes to User name.
    [Fact]
    public async Task Should_AskForTheLogin_AndPutTheCursorInUserName_OnABoardWithOne()
    {
        await using var simulator = new Vm201Simulator("admin", "secret", NullLogger.Instance);
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Velleman", "vm201");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board asks for a user name and password.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.With("id", "username")));
        Assert.Equal(["username"], page.Focused.Where(id => id is "password" or "username"));
    }

    // The setup guide: the same step, and "Choose model" goes back to "Which relay board?".
    [Fact]
    public async Task Should_GoBackToTheModel_InTheSetupGuide()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await using var app = new TestWebApp(
            settings: new Dictionary<string, string?>
            {
                ["CloudflareAccess:TeamDomain"] = "myteam.cloudflareaccess.com",
                ["CloudflareAccess:Audience"] = "aud",
                ["Devantech:Host"] = "127.0.0.1",
                ["Devantech:Port"] = simulator.Port.ToString(CultureInfo.InvariantCulture),
            },
            setUp: false);
        app.SaveBeforeStart(new Installation { AccessType = Contactor.Web.Features.Cloudflare.CloudflareMethods.TunnelId, SignInType = Contactor.Web.Features.Cloudflare.CloudflareMethods.AccessId });
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);
        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");
        await page.ClickAsync(Button("Next"));
        await ChooseAsync(page, "Devantech", "devantech-eth008");
        await page.ClickAsync(Button("Next"));

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board at this address is an ETH484-B, not an ETH008-B.", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(Button("Choose model"));
        Assert.Contains("Which relay board?", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // Settings: a login problem opens the login fields and takes the cursor only after the person's own Save, once
    // for each Save.
    [Fact]
    public async Task Should_OpenTheLogin_OnlyAfterTheirOwnSave_OnSettings()
    {
        await using var simulator = Started(DevantechModels.Eth008, "apple");
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = John,
                ["Board:Type"] = "devantech-eth008",
                ["Devantech:Host"] = "127.0.0.1",
                ["Devantech:Port"] = simulator.Port.ToString(CultureInfo.InvariantCulture),
            });
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/settings", Cancel);
        }

        var board = app.Services.GetRequiredService<BoardRegistry>().All[0];
        await BoardTestRig.WaitUntilAsync(() => board.State.Problem is not null);
        using var page = await PageRenderer.StartAsync<BoardForm>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardForm.BoardId)] = board.BoardId });
        Assert.Contains("The board asks for a password.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "password")));
        Assert.Empty(page.Focused);

        await page.SubmitAsync(PageRenderer.With("class", "card"));
        Assert.True(await page.HasAsync(PageRenderer.With("id", "password")));
        Assert.Equal(["password"], page.Focused);

        await page.TypeAsync(PageRenderer.With("id", "password"), "pear");
        await page.SubmitAsync(PageRenderer.With("class", "card"));
        await BoardTestRig.WaitUntilAsync(() => page.Focused.Count == 2);
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);

        await page.TypeAsync(PageRenderer.With("id", "password"), "apple");
        await page.SubmitAsync(PageRenderer.With("class", "card"));
        await BoardTestRig.WaitUntilAsync(() => board.State.IsConnected);
        Assert.Equal(2, page.Focused.Count);
        Assert.Empty(page.Errors);
    }

    // A problem that comes long after the Save, e.g. once the board is plugged in, moves nothing: the person may be
    // typing by then. One within the setup guide's 10 s does.
    [Fact]
    public async Task Should_MoveNothing_ForAProblemLongAfterTheSave()
    {
        var time = new ManualTimeProvider();
        var board = new FakeRelayBoard(connected: false);
        var needed = new ConnectionProblem(ConnectionProblemKind.LoginNeeded, Vm201Model.Model);
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services => services.AddSingleton<TimeProvider>(time));
        var id = await app.Services.FirstBoardIdAsync();
        await app.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(board).Build(id, Vm201Model.Model), Cancel);
        using var page = await PageRenderer.StartAsync<BoardForm>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardForm.BoardId)] = id });

        await page.SubmitAsync(PageRenderer.With("class", "card"));
        time.Advance(BoardForm.LoginFocusWindow + TimeSpan.FromSeconds(1));
        board.SetProblem(needed);
        await BoardTestRig.WaitUntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("The board asks for a user name and password.", StringComparison.Ordinal));
        Assert.Empty(page.Focused);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "username")));

        board.SetProblem(null);
        await page.SubmitAsync(PageRenderer.With("class", "card"));
        board.SetProblem(needed);
        await BoardTestRig.WaitUntilAsync(() => page.Focused.Count == 1);
        Assert.Equal(["username"], page.Focused);
        Assert.Empty(page.Errors);
    }

    // Too many wrong logins opens the login fields after a Save, as a wrong login does; old firmware isn't fixed there.
    [Theory]
    [InlineData(ConnectionProblemKind.TooManyTries, true)]
    [InlineData(ConnectionProblemKind.FirmwareTooOld, false)]
    public async Task Should_OpenTheLogin_OnlyForAProblemAboutIt(ConnectionProblemKind kind, bool opens)
    {
        var board = new FakeRelayBoard(connected: false);
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John });
        var id = await app.Services.FirstBoardIdAsync();
        await app.Services.GetRequiredService<BoardRegistry>().ReplaceAsync(new FakeBoardParts(board).Build(id, Vm201Model.Model), Cancel);
        using var page = await PageRenderer.StartAsync<BoardForm>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardForm.BoardId)] = id });

        await page.SubmitAsync(PageRenderer.With("class", "card"));
        board.SetProblem(new ConnectionProblem(kind, Vm201Model.Model, MinFirmware: "1.7.0"));
        var text = kind == ConnectionProblemKind.TooManyTries ? "Too many wrong logins." : "The board's firmware is too old.";
        await BoardTestRig.WaitUntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains(text, StringComparison.Ordinal));

        Assert.Equal(opens, await page.HasAsync(PageRenderer.With("id", "username")));
        string?[] focused = opens ? ["username"] : [];
        Assert.Equal(focused, page.Focused);
        Assert.Empty(page.Errors);
    }

    private static DevantechSimulator Started(BoardModel model, string? password = null)
    {
        var simulator = new DevantechSimulator(model, password, NullLogger.Instance);
        simulator.Start();
        return simulator;
    }

    // "Where is the board?" on the simulator's address.
    private static async Task AtAsync(PageRenderer page, int port)
    {
        await page.TypeAsync(PageRenderer.With("id", "host"), "127.0.0.1");
        await page.TypeAsync(PageRenderer.With("id", "port"), port.ToString(CultureInfo.InvariantCulture));
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
    }

    // "Brand", then "Model", as a person picks them.
    private static async Task ChooseAsync(PageRenderer page, string brand, string model)
    {
        await page.ClickAsync(PageRenderer.With("aria-labelledby", "brand-label brand-value"));
        await page.ClickAsync(PageRenderer.With("value", brand));
        await page.ClickAsync(PageRenderer.With("value", model));
    }

    // The board filter's record of what it shows, fed with the board's states as the registry tells them.
    private sealed class ShownBoardStatesProbe
    {
        private readonly Contactor.Web.Features.Relays.ShownBoardStates _shown = new();
        private readonly FakeRelayBoard _board;

        public ShownBoardStatesProbe(FakeRelayBoard board)
        {
            _board = board;
            _shown.Changes(new BoardStateChange(1, board.State));
        }

        public bool Changed()
        {
            return _shown.Changes(new BoardStateChange(1, _board.State));
        }
    }
}
