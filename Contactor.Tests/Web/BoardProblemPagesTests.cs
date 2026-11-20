using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
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
        { new ConnectionProblem(ConnectionProblemKind.WrongModel, DevantechModels.Eth008), "The board at this address is another model, not the ETH008-B.", "Wrong model" },
        { new ConnectionProblem(ConnectionProblemKind.LoginNeeded, DevantechModels.Eth008), "The board asks for a password.", "Needs a password" },
        { new ConnectionProblem(ConnectionProblemKind.LoginNeeded, Vm201Model.Model), "The board asks for a user name and password.", "Needs a login" },
        { new ConnectionProblem(ConnectionProblemKind.LoginRefused, DevantechModels.Eth008), "The password is wrong.", "Wrong password" },
        { new ConnectionProblem(ConnectionProblemKind.LoginRefused, Vm201Model.Model), "The user name or password is wrong.", "Wrong login" },
        { new ConnectionProblem(ConnectionProblemKind.FirmwareTooOld, DevantechModels.Eth008, MinFirmware: "1.7.0"), "The board's firmware is too old. Update it to 1.7.0 or later on the board's own web page.", "Old firmware" },
        { new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4), "The board at this address has 4 relays, not 2.", "Wrong model" },
        { new ConnectionProblem(ConnectionProblemKind.CoverProfile, ShellyModels.TwoRelays), "The board is set up to drive a cover, not relays. Set its profile to \"switch\" on its own web page, then check again.", "Set up for a cover" },
        { new ConnectionProblem(ConnectionProblemKind.AskedToWait, DevantechModels.Eth008), "The board asks Contactor to wait a while. If you just changed the password, check it.", "Asked to wait" },
        { null, "No contact with the relay board at", "No contact yet" },
    };

    // A Shelly added in the app: its password, then a 429, which says to wait and doesn't claim the password is wrong,
    // then the right one goes on, and the app switches its relays.
    [Fact]
    public async Task Should_AddAShelly_AndSayToWait_WhenItAsks()
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
        Assert.Contains("The board asks Contactor to wait a while. If you just changed the password, check it.", await page.TextAsync(), StringComparison.Ordinal);

        simulator.Throttle = false;
        await page.TypeAsync(PageRenderer.With("id", "password"), "apple");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);

        var shelly = boards.App.Services.GetRequiredService<BoardRegistry>().All[^1];
        Assert.Equal(ChannelMask.Of(2), await shelly.TurnOnAsync(ChannelMask.Of(2), Cancel));
        Assert.Equal((byte)0b10, simulator.Relays);
    }

    // A Shelly that asks to wait from the start: the step says so, and leaves "no password" ticked, since a 429 doesn't
    // say the password is wrong.
    [Fact]
    public async Task Should_SayToWait_AndLeaveTheLoginAsItIs_WhenAShellyAsks()
    {
        await using var simulator = new ShellySimulator(ShellyModels.OneRelay, "apple", NullLogger.Instance) { Throttle = true };
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-1");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);

        await page.ClickAsync(Button("Next"));

        Assert.Contains("The board asks Contactor to wait a while. If you just changed the password, check it.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(simulator.ThrottledCount > 0);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "password")));
        Assert.DoesNotContain(page.Focused, id => id is "password" or "username");
        Assert.True(await page.HasAsync(Button("Check again")));
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

    // Issue #522: a refused password isn't tried again by itself, so "Check again" in the guide sends it again. That the
    // poller then sends nothing more is proven by the poller's and the clients' tests, on a fake clock.
    [Fact]
    public async Task Should_SendTheRefusedPasswordAgain_WhenAPersonChecksAgain()
    {
        await using var simulator = new ShellySimulator(ShellyModels.OneRelay, "apple", NullLogger.Instance);
        simulator.Start();
        await using var boards = await TwoBoards.StartAsync();
        using var page = await PageRenderer.StartAsync<BoardNew>(boards.App.Services, John, "John");
        await ChooseAsync(page, "Shelly", "shelly-1");
        await page.ClickAsync(Button("Next"));
        await AtAsync(page, simulator.Port);
        await page.ClickAsync(Button("Next"));
        await page.TypeAsync(PageRenderer.With("id", "password"), "pear");
        await page.ClickAsync(Button("Next"));
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);
        var denied = simulator.DeniedCount;

        await page.ClickAsync(Button("Check again"));

        Assert.Equal(denied + 1, simulator.DeniedCount);
        Assert.Contains("The password is wrong.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // On Settings, Save with the same password is the person's "Check again": it sends the password again.
    [Fact]
    public async Task Should_SendTheRefusedPasswordAgain_WhenTheSameIsSavedOnSettings()
    {
        await using var simulator = new ShellySimulator(ShellyModels.OneRelay, "apple", NullLogger.Instance);
        simulator.Start();
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                [TestWebApp.SignedInAs] = John,
                ["Board:Type"] = "shelly-1",
                ["Shelly:Host"] = "127.0.0.1",
                ["Shelly:Port"] = simulator.Port.ToString(CultureInfo.InvariantCulture),
                ["Shelly:Password"] = "pear",
            });
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/settings", Cancel);
        }

        var board = app.Services.GetRequiredService<BoardRegistry>().All[0];
        await BoardTestRig.WaitUntilAsync(() => board.State.Problem is { Kind: ConnectionProblemKind.LoginRefused });
        Assert.Equal(1, simulator.DeniedCount);
        using var page = await PageRenderer.StartAsync<BoardForm>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardForm.BoardId)] = board.BoardId });

        await page.SubmitAsync(PageRenderer.With("class", "card"));

        await BoardTestRig.WaitUntilAsync(() => simulator.DeniedCount == 2 && board.State.Problem is not null);
        Assert.Equal(2, simulator.DeniedCount);
        Assert.False(board.State.IsConnected);
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

    // Only a login needed or refused opens the login fields after a Save. Being asked to wait doesn't say the login is
    // wrong, and old firmware isn't fixed there.
    [Theory]
    [InlineData(ConnectionProblemKind.LoginRefused, true)]
    [InlineData(ConnectionProblemKind.AskedToWait, false)]
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
        var text = kind switch
        {
            ConnectionProblemKind.LoginRefused => "The user name or password is wrong.",
            ConnectionProblemKind.AskedToWait => "The board asks Contactor to wait a while.",
            _ => "The board's firmware is too old.",
        };
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
