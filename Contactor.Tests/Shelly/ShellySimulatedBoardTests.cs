using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Shelly;

/// <summary>The simulated Shelly as the docs describe the device, spoken to with raw HTTP.</summary>
public sealed class ShellySimulatedBoardTests
{
    private const string Password = "pw";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_AnswerAsTheDocsShow()
    {
        await using var device = new ShellySimulator(ShellyModels.TwoRelays);
        device.Start();

        var info = await PostAsync(device.Port, "{\"id\":1,\"src\":\"user_1\",\"method\":\"Shelly.GetDeviceInfo\"}");
        Assert.StartsWith("HTTP/1.1 200 OK", info, StringComparison.Ordinal);
        Assert.Contains("Connection: close", info, StringComparison.Ordinal);
        Assert.EndsWith(
            "{\"id\":1,\"src\":\"shellysim2-0a1b2c3d4e5f\",\"dst\":\"user_1\",\"result\":{\"id\":\"shellysim2-0a1b2c3d4e5f\",\"mac\":\"0A1B2C3D4E5F\",\"model\":\"SIM-2\",\"gen\":2,\"fw_id\":\"20260101-000000/2.0.0-sim\",\"ver\":\"2.0.0\",\"app\":\"Simulator\",\"auth_en\":false,\"auth_domain\":null}}",
            info,
            StringComparison.Ordinal);

        // "RPC Channels": the answer to Switch.Set; the string id comes back as it went.
        var set = await PostAsync(device.Port, "{\"id\":\"a\",\"method\":\"Switch.Set\",\"params\":{\"id\":1,\"on\":true}}");
        Assert.EndsWith("{\"id\":\"a\",\"src\":\"shellysim2-0a1b2c3d4e5f\",\"result\":{\"was_on\":false}}", set, StringComparison.Ordinal);
        Assert.Equal(ChannelMask.Of(2), device.State.Relays);

        var status = Body(await PostAsync(device.Port, "{\"id\":2,\"method\":\"Shelly.GetStatus\"}"));
        var frame = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(status), 2);
        Assert.Equal(new ShellySwitches(2, 0b10, 10), ShellyJson.ReadSwitches(frame.Result!.Value));
    }

    [Fact]
    public async Task Should_AnswerTheDocsErrors()
    {
        await using var device = new ShellySimulator(ShellyModels.TwoRelays) { FailRelays = 0b01 };
        device.Start();

        Assert.EndsWith("\"error\":{\"code\":-109,\"message\":\"Overtemp condition present\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.Set\",\"params\":{\"id\":0,\"on\":true}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-105,\"message\":\"Bad id=2\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.Set\",\"params\":{\"id\":2,\"on\":true}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-103,\"message\":\"Missing required 'id'\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.Set\",\"params\":{\"on\":true}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-103,\"message\":\"Missing required 'on'\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.Set\",\"params\":{\"id\":1}}"), StringComparison.Ordinal);
        Assert.Equal(ChannelMask.None, device.State.Relays);
    }

    [Fact]
    public async Task Should_AnswerTheSwitchSettings_AsTheDocsShow_AndGrowCfgRevOnAChange()
    {
        await using var device = new ShellySimulator(ShellyModels.TwoRelays);
        device.Start();
        Assert.Equal(10, device.ConfigRevision);

        var config = Body(await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.GetConfig\",\"params\":{\"id\":1}}"));
        Assert.Equal(
            "{\"id\":1,\"src\":\"shellysim2-0a1b2c3d4e5f\",\"result\":{\"id\":1,\"name\":null,\"in_mode\":\"follow\",\"in_locked\":false,\"initial_state\":\"off\",\"auto_on\":false,\"auto_on_delay\":60,\"auto_off\":false,\"auto_off_delay\":60,\"power_restore_delay_enable\":false,\"power_restore_delay\":60}}",
            config);

        // The docs' SetConfig answer; cfg_rev grows only when the value changed.
        var set = await PostAsync(device.Port, "{\"id\":2,\"method\":\"Switch.SetConfig\",\"params\":{\"id\":1,\"config\":{\"initial_state\":\"match_input\"}}}");
        Assert.EndsWith("\"result\":{\"restart_required\":false}}", set, StringComparison.Ordinal);
        Assert.Equal("match_input", device.InitialState(2));
        Assert.Equal("off", device.InitialState(1));
        Assert.Equal(11, device.ConfigRevision);
        await PostAsync(device.Port, "{\"id\":3,\"method\":\"Switch.SetConfig\",\"params\":{\"id\":1,\"config\":{\"initial_state\":\"match_input\"}}}");
        Assert.Equal(11, device.ConfigRevision);

        // Set at the device, and another setting: both grow it.
        device.SetInitialState(1, "on");
        device.ChangeAnotherSetting();
        Assert.Equal(13, device.ConfigRevision);
        var status = Body(await PostAsync(device.Port, "{\"id\":4,\"method\":\"Shelly.GetStatus\"}"));
        Assert.Equal(13, ShellyJson.ReadSwitches(ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(status), 4).Result!.Value).ConfigRevision);
    }

    [Fact]
    public async Task Should_RefuseSwitchSettings_ItCantTake()
    {
        await using var device = new ShellySimulator(ShellyModels.TwoRelays) { FailConfigRelays = 0b10 };
        device.Start();

        Assert.EndsWith("\"error\":{\"code\":-105,\"message\":\"Bad id=2\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.GetConfig\",\"params\":{\"id\":2}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-103,\"message\":\"Missing required 'id'\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.GetConfig\"}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-109,\"message\":\"Overtemp condition present\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.GetConfig\",\"params\":{\"id\":1}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-103,\"message\":\"Missing required 'config'\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.SetConfig\",\"params\":{\"id\":0}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-103,\"message\":\"Invalid argument 'initial_state'\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.SetConfig\",\"params\":{\"id\":0,\"config\":{\"initial_state\":\"sometimes\"}}}"), StringComparison.Ordinal);
        Assert.EndsWith("\"error\":{\"code\":-109,\"message\":\"Overtemp condition present\"}}", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Switch.SetConfig\",\"params\":{\"id\":1,\"config\":{\"initial_state\":\"on\"}}}"), StringComparison.Ordinal);

        Assert.Equal("off", device.InitialState(1));
        Assert.Equal("off", device.InitialState(2));
        Assert.Equal(10, device.ConfigRevision);
    }

    [Fact]
    public async Task Should_AskForTheDigest_AsTheDocsShow_ButNotForTheDeviceInfo()
    {
        await using var device = new ShellySimulator(ShellyModels.OneRelay, Password, NullLogger.Instance);
        device.Start();

        Assert.StartsWith("HTTP/1.1 200", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetDeviceInfo\"}"), StringComparison.Ordinal);
        var refused = await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}");
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", refused, StringComparison.Ordinal);
        var header = Header(refused, "WWW-Authenticate")!;
        Assert.Matches("^Digest qop=\"auth\", realm=\"shellysim1-0a1b2c3d4e5f\", nonce=\"[A-Za-z0-9+/=]+\", algorithm=SHA-256$", header);

        var challenge = ShellyDigest.ParseChallenge([header])!;
        var ok = await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 1, "1"));
        Assert.StartsWith("HTTP/1.1 200", ok, StringComparison.Ordinal);

        // The same nonce with a growing nc; an nc used before is refused.
        Assert.StartsWith("HTTP/1.1 200", await PostAsync(device.Port, "{\"id\":2,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 2, "2")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 401", await PostAsync(device.Port, "{\"id\":3,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 2, "3")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 401", await PostAsync(device.Port, "{\"id\":4,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, "wrong", 5, "4")), StringComparison.Ordinal);
        Assert.Equal(1, device.DeniedCount);
    }

    [Fact]
    public async Task Should_SayStale_ForANonceAnHourOld()
    {
        var time = new ManualTimeProvider();
        await using var device = new ShellySimulator(ShellyModels.OneRelay, Password, NullLogger.Instance) { TimeProvider = time };
        device.Start();
        var challenge = ShellyDigest.ParseChallenge([Header(await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), "WWW-Authenticate")!])!;

        time.Advance(ShellySimulator.NonceLifetime);
        var stale = await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 1, "1"));

        Assert.StartsWith("HTTP/1.1 401", stale, StringComparison.Ordinal);
        Assert.EndsWith(", stale=true", Header(stale, "WWW-Authenticate"), StringComparison.Ordinal);
        Assert.Equal(0, device.DeniedCount);
    }

    [Fact]
    public async Task Should_TakeANonceOnce_BeforeFirmware2()
    {
        await using var device = new ShellySimulator(ShellyModels.OneRelay, Password, NullLogger.Instance) { Firmware = "1.7.1" };
        device.Start();
        var header = Header(await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), "WWW-Authenticate")!;

        // "Legacy HTTP Response": a numeric nonce.
        Assert.Matches("^Digest qop=\"auth\", realm=\"shellysim1-0a1b2c3d4e5f\", nonce=\"[0-9]+\", algorithm=SHA-256$", header);
        var challenge = ShellyDigest.ParseChallenge([header])!;
        Assert.StartsWith("HTTP/1.1 200", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 1, "1")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 401", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, Password, 2, "1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Throttle_AfterTooManyWrongPasswords()
    {
        var time = new ManualTimeProvider();
        await using var device = new ShellySimulator(ShellyModels.OneRelay, Password, NullLogger.Instance) { TimeProvider = time };
        device.Start();

        // "1-10 failures: No delay; 11-20 failures: 10 seconds between attempts".
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            var challenge = ShellyDigest.ParseChallenge([Header(await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), "WWW-Authenticate")!])!;
            Assert.StartsWith("HTTP/1.1 401", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}", ShellyDigest.Authorization(challenge, "wrong", 1, "1")), StringComparison.Ordinal);
        }

        Assert.StartsWith("HTTP/1.1 429", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), StringComparison.Ordinal);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 401", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), StringComparison.Ordinal);
        Assert.Equal(11, device.DeniedCount);
        Assert.Equal(1, device.ThrottledCount);
    }

    [Fact]
    public async Task Should_AnswerNotFound_AsAGen1Device()
    {
        await using var device = new ShellySimulator(ShellyModels.OneRelay) { Generation = null };
        device.Start();

        Assert.StartsWith("HTTP/1.1 404", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetDeviceInfo\"}"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"method\":\"Shelly.GetStatus\"}")]
    [InlineData("")]
    public async Task Should_SurviveMalformedRequests(string body)
    {
        await using var device = new ShellySimulator(ShellyModels.OneRelay);
        device.Start();

        Assert.StartsWith("HTTP/1.1 400", await PostAsync(device.Port, body), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 404", await RawAsync(device.Port, "GET /index.html HTTP/1.1\r\n\r\n"), StringComparison.Ordinal);
        Assert.Equal("", await RawAsync(device.Port, "garbage\r\n\r\n"));
        Assert.Equal("", await RawAsync(device.Port, "POST /rpc HTTP/1.1\r\nContent-Length: 99999\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_CloseAConnection_ThatSendsTooMuch()
    {
        await using var device = new ShellySimulator(ShellyModels.OneRelay);
        device.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", device.Port, Cancel);
        var stream = tcp.GetStream();
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /" + new string('a', 20_000)), Cancel);
        }
        catch (IOException)
        {
            // Closed while still writing: just as good.
        }

        Assert.Equal("", await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Should_TellWhoAskedRecently_AndWhatChangedAtTheDevice()
    {
        await using var device = new ShellySimulator(ShellyModels.ThreeRelays) { TimeProvider = new ManualTimeProvider() };
        var changes = 0;
        device.Changed += (_, _) => Interlocked.Increment(ref changes);
        device.Start();

        await device.SwitchRelayAsync(3, true);
        await device.SwitchRelayAsync(3, true);
        await PostAsync(device.Port, "{\"id\":1,\"method\":\"Shelly.GetStatus\"}");

        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.Equal(ChannelMask.Of(3), device.State.Relays);
        Assert.Equal(["127.0.0.1"], device.State.Clients);
        Assert.False(device.HasLogin);
        Assert.Same(ShellyModels.ThreeRelays, device.Model);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SwitchRelayAsync(4, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SetInputAsync(1, true));
        Assert.Throws<ArgumentException>(() => new ShellySimulator(Contactor.Boards.Devantech.DevantechModels.Eth002));
    }

    private static Task<string> PostAsync(int port, string body, string? authorization = null)
    {
        var content = Encoding.UTF8.GetBytes(body);
        var auth = authorization is null ? "" : $"Authorization: {authorization}\r\n";
        return RawAsync(port, $"POST /rpc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n{auth}Content-Length: {content.Length}\r\n\r\n{body}");
    }

    private static async Task<string> RawAsync(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), Cancel);
        return await ReadAllAsync(stream);
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, Cancel)) > 0)
            {
                memory.Write(buffer, 0, read);
            }
        }
        catch (IOException)
        {
            // Reset by the simulator: what came before counts.
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string Body(string response)
    {
        return response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
    }

    private static string? Header(string response, string name)
    {
        return response.Split("\r\n").FirstOrDefault(l => l.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase))?[(name.Length + 2)..];
    }
}
