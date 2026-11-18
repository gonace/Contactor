using System.Text;
using Contactor.Boards;
using Contactor.Boards.Shelly;

namespace Contactor.Tests.Shelly;

/// <summary>The JSON-RPC frames, read with the docs' own examples, byte for byte, and refused when they are anything else.</summary>
public sealed class ShellyJsonTests
{
    // "RPC Channels", "POST request with entire frame as payload": the answer to Switch.Set, as the docs print it.
    private const string ChannelsSwitchSetAnswer = "{\"id\":0,\"src\":\"shellydev1-c8f09e47e6f0\",\"result\":{\"was_on\":true}}";

    // "RPC Protocol", "Response Frame", example 1.
    private const string ProtocolResultFrame = """
        {
           "id": 2,
           "src": "shellypro4pm-f008d1d8b8b8",
           "dst": "user_1",
           "result": {
              "was_on": false
           }
        }
        """;

    // "RPC Protocol", "Response Frame", example 2.
    private const string ProtocolErrorFrame = """
        {
           "id": 1,
           "src": "shellypro4pm-f008d1d8b8b8",
           "dst": "user_1",
           "error": {
              "code": -105,
              "message": "Bad id=12"
           }
        }
        """;

    // "Common Errors", "-109: FAILED PRECONDITION".
    private const string FailedPreconditionFrame = """
        {
        "id"
        :
        1
        ,
        "src"
        :
        "shellypro4pm-f008d1d8b8b8"
        ,
        "dst"
        :
        "user_1"
        ,
        "error"
        :
        {
        "code"
        :
        -109
        ,
        "message"
        :
        "Overtemp condition present"
        }
        }
        """;

    // "Shelly", "HTTP Endpoint: /shelly": "equivalent to invoking Shelly.GetDeviceInfo".
    private const string DeviceInfoExample = """
        {
          "id": "shellypro4pm-f008d1d8b8b8",
          "mac": "F008D1D8B8B8",
          "model": "SPSW-004PE16EU",
          "gen": 2,
          "fw_id": "20210720-153353/0.6.7-gc36674b",
          "ver": "0.6.7",
          "app": "FourPro",
          "auth_en": true,
          "auth_domain": "shellypro4pm-f008d1d8b8b8"
        }
        """;

    // "Shelly", "Shelly.GetStatus example", the HTTP GET response: the result of a Pro 4PM, verbatim.
    private const string StatusExample = """
        {
          "ble": {},
          "cloud": {
            "connected": false
          },
          "eth": {
            "ip": "10.33.55.170"
          },
          "input:0": {
            "id": 0,
            "state": false
          },
          "input:1": {
            "id": 1,
            "state": false
          },
          "input:2": {
            "id": 2,
            "state": false
          },
          "input:3": {
            "id": 3,
            "state": false
          },
          "mqtt": {
            "connected": false
          },
          "switch:0": {
            "id": 0,
            "source": "timer",
            "output": true,
            "timer_started_at": 1626935739.79,
            "timer_duration": 60,
            "apower": 8.9,
            "voltage": 237.5,
            "aenergy": {
              "total": 6.532,
              "by_minute": [
                45.199,
                47.141,
                88.397
              ],
              "minute_ts": 1626935779
            },
            "temperature": {
              "tC": 23.5,
              "tF": 74.4
            }
          },
          "switch:1": {
            "id": 1,
            "source": "init",
            "output": false,
            "apower": 0,
            "voltage": 237.5,
            "aenergy": {
              "total": 0,
              "by_minute": [
                0,
                0,
                0
              ],
              "minute_ts": 1626935779
            },
            "temperature": {
              "tC": 23.5,
              "tF": 74.4
            }
          },
          "switch:2": {
            "id": 2,
            "source": "timer",
            "output": false,
            "timer_started_at": 1626935591.8,
            "timer_duration": 345,
            "apower": 0,
            "voltage": 237.5,
            "aenergy": {
              "total": 0.068,
              "by_minute": [
                0,
                0,
                0
              ],
              "minute_ts": 1626935779
            },
            "temperature": {
              "tC": 23.5,
              "tF": 74.4
            }
          },
          "switch:3": {
            "id": 3,
            "source": "init",
            "output": false,
            "apower": 0,
            "voltage": 237.5,
            "aenergy": {
              "total": 0,
              "by_minute": [
                0,
                0,
                0
              ],
              "minute_ts": 1626935779
            },
            "temperature": {
              "tC": 23.5,
              "tF": 74.4
            }
          },
          "sys": {
            "mac": "A8032ABE54DC",
            "restart_required": false,
            "time": "16:06",
            "unixtime": 1650035219,
            "last_sync_ts": 1650035219,
            "uptime": 11081,
            "ram_size": 254744,
            "ram_free": 151560,
            "fs_size": 458752,
            "fs_free": 180224,
            "cfg_rev": 26,
            "kvs_rev": 2725,
            "schedule_rev": 0,
            "webhook_rev": 0,
            "available_updates": {
              "stable": {
                "version": "0.10.1"
              }
            }
          },
          "wifi": {
            "sta_ip": null,
            "status": "disconnected",
            "ssid": null,
            "rssi": 0
          },
          "ws": {
            "connected": true
          }
        }
        """;

    // "Switch", "Switch.GetConfig example", the HTTP GET response, as the docs print it.
    private const string GetConfigExample = """
        {
          "id": 0,
          "name": "Switchdsds",
          "in_mode": "follow",
          "in_locked": false,
          "initial_state": "match_input",
          "auto_on": false,
          "auto_on_delay": 60,
          "auto_off": false,
          "auto_off_delay": 60,
          "power_restore_delay_enable": false,
          "power_restore_delay": 60,
          "autorecover_voltage_errors": false,
          "power_limit": 4480,
          "voltage_limit": 280,
          "undervoltage_limit": 0,
          "current_limit": 16,
          "counts": {
            "enable": true,
            "power_thr": 100
          }
        }
        """;

    // "Switch", "Switch.SetConfig example", the HTTP GET response.
    private const string SetConfigExample = """
        {
          "restart_required": false
        }
        """;

    [Fact]
    public void Should_ReadTheDocsSwitchSetAnswers()
    {
        var channels = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(ChannelsSwitchSetAnswer), 0);
        Assert.Null(channels.Error);
        Assert.True(channels.Result!.Value.GetProperty("was_on").GetBoolean());

        var protocol = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(ProtocolResultFrame), 2);
        Assert.False(protocol.Result!.Value.GetProperty("was_on").GetBoolean());
    }

    [Fact]
    public void Should_ReadTheDocsErrorFrames_WithTheDevicesOwnMessage()
    {
        var notFound = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(ProtocolErrorFrame), 1);
        Assert.Equal(new ShellyRpcError(-105, "Bad id=12"), notFound.Error);
        Assert.Null(notFound.Result);

        var overtemp = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(FailedPreconditionFrame), 1).Error!;
        Assert.Equal(ShellyApi.FailedPrecondition, overtemp.Code);
        Assert.Equal("Overtemp condition present (error -109)", overtemp.ToString());
    }

    [Fact]
    public void Should_ReadTheDocsDeviceInfo()
    {
        using var document = System.Text.Json.JsonDocument.Parse(DeviceInfoExample);

        var info = ShellyJson.ReadDeviceInfo(document.RootElement);

        Assert.Equal(new ShellyDeviceInfo("shellypro4pm-f008d1d8b8b8", "SPSW-004PE16EU", 2, "0.6.7", null), info);
    }

    [Fact]
    public void Should_ReadTheDocsStatus_SwitchZeroAsRelayOne()
    {
        using var document = System.Text.Json.JsonDocument.Parse(StatusExample);

        // sys.cfg_rev is 26 in the docs' example.
        Assert.Equal(new ShellySwitches(4, 0b0001, 26), ShellyJson.ReadSwitches(document.RootElement));
    }

    [Fact]
    public void Should_WriteTheDocsSwitchSetRequest()
    {
        // "RPC Protocol", request example 2, with the src the app always sends.
        Assert.Equal(
            "{\"id\":2,\"src\":\"contactor\",\"method\":\"Switch.Set\",\"params\":{\"id\":1,\"on\":true}}",
            Encoding.UTF8.GetString(ShellyJson.WriteSwitchSet(2, relay: 2, on: true)));
        Assert.Equal("{\"id\":7,\"src\":\"contactor\",\"method\":\"Shelly.GetStatus\"}", Encoding.UTF8.GetString(ShellyJson.WriteRequest(7, ShellyApi.GetStatus)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchSet(1, relay: 5, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchSet(1, relay: 0, on: true));
    }

    [Fact]
    public void Should_ReadTheDocsGetConfig_AsSwitchZerosPowerOnState()
    {
        using var document = System.Text.Json.JsonDocument.Parse(GetConfigExample);

        Assert.Equal(PowerOnState.FollowsInput, ShellyJson.ReadPowerOn(document.RootElement, relay: 1));

        // An answer about switch 0 isn't relay 2's.
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadPowerOn(document.RootElement, relay: 2));
    }

    [Fact]
    public void Should_ReadTheDocsSetConfigAnswer()
    {
        var frame = ShellyJson.ReadFrame(Encoding.UTF8.GetBytes($"{{\"id\":4,\"result\":{SetConfigExample}}}"), 4);

        Assert.False(frame.Result!.Value.GetProperty("restart_required").GetBoolean());
    }

    [Fact]
    public void Should_WriteTheDocsConfigRequests_WithOnlyInitialState()
    {
        // "Switch", the curl examples, with the src the app always sends and only initial_state in config.
        Assert.Equal(
            "{\"id\":1,\"src\":\"contactor\",\"method\":\"Switch.GetConfig\",\"params\":{\"id\":0}}",
            Encoding.UTF8.GetString(ShellyJson.WriteSwitchGetConfig(1, relay: 1)));
        Assert.Equal(
            "{\"id\":1,\"src\":\"contactor\",\"method\":\"Switch.SetConfig\",\"params\":{\"id\":3,\"config\":{\"initial_state\":\"restore_last\"}}}",
            Encoding.UTF8.GetString(ShellyJson.WriteSwitchSetPowerOn(1, relay: 4, PowerOnState.AsBefore)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchGetConfig(1, relay: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchSetPowerOn(1, relay: 0, PowerOnState.Off));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchSetPowerOn(1, relay: 1, (PowerOnState)4));
    }

    [Theory]
    [InlineData("off", PowerOnState.Off)]
    [InlineData("restore_last", PowerOnState.AsBefore)]
    [InlineData("on", PowerOnState.On)]
    [InlineData("match_input", PowerOnState.FollowsInput)]
    public void Should_MapEachInitialState_BothWays(string initialState, PowerOnState state)
    {
        Assert.Equal(state, ShellyApi.ToPowerOnState(initialState));
        Assert.Equal(initialState, ShellyApi.ToInitialState(state));
        Assert.True(ShellyApi.PowerOn.Has(state));
    }

    [Theory]
    [InlineData("{\"id\":0,\"initial_state\":\"ON\"}")]
    [InlineData("{\"id\":0,\"initial_state\":\"\"}")]
    [InlineData("{\"id\":0,\"initial_state\":1}")]
    [InlineData("{\"id\":0,\"initial_state\":null}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"initial_state\":\"later\"}")]
    public void Should_ReadAnInitialStateTheDocsDontList_AsUnknown(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Null(ShellyJson.ReadPowerOn(document.RootElement, relay: 1));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"id\":\"0\",\"initial_state\":\"on\"}")]
    [InlineData("{\"id\":1,\"initial_state\":\"on\"}")]
    [InlineData("{\"id\":0.5,\"initial_state\":\"on\"}")]
    public void Should_RefuseSettingsThatArentThisSwitchs(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadPowerOn(document.RootElement, relay: 1));
    }

    [Theory]
    [InlineData("{\"switch:0\":{\"output\":true}}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{}}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":[]}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{\"cfg_rev\":\"7\"}}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{\"cfg_rev\":7.5}}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{\"cfg_rev\":1e30}}", null)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{\"cfg_rev\":0}}", 0L)]
    [InlineData("{\"switch:0\":{\"output\":true},\"sys\":{\"cfg_rev\":9007199254740993}}", 9007199254740993L)]
    public void Should_ReadCfgRev_OnlyAsAWholeNumber(string json, long? revision)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(new ShellySwitches(1, 1, revision), ShellyJson.ReadSwitches(document.RootElement));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"id\":2,\"result\":{}}")]
    [InlineData("{\"id\":\"1\",\"result\":{}}")]
    [InlineData("{\"result\":{}}")]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"id\":1,\"result\":{},\"error\":{\"code\":1,\"message\":\"x\"}}")]
    [InlineData("{\"id\":1,\"result\":null}")]
    [InlineData("{\"id\":1,\"result\":true}")]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"x\"}}")]
    [InlineData("{\"id\":1,\"error\":\"x\"}")]
    public void Should_RefuseAFrameThatIsntAnAnswerToTheRequest(string json)
    {
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(json), 1));
        Assert.Null(ShellyJson.TryReadError(Encoding.UTF8.GetBytes(json), 1));
    }

    [Fact]
    public void Should_RefuseTooLongOrTooDeep()
    {
        var huge = new byte[ShellyApi.MaxResponseBytes + 1];
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadFrame(huge, 1));

        var deep = "{\"id\":1,\"result\":" + new string('[', 100) + new string(']', 100) + "}";
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadFrame(Encoding.UTF8.GetBytes(deep), 1));
    }

    [Theory]
    [InlineData("{\"switch:0\":{\"output\":true},\"switch:2\":{\"output\":false}}")]
    [InlineData("{\"switch:1\":{\"output\":true}}")]
    [InlineData("{\"switch:0\":{\"output\":\"on\"}}")]
    [InlineData("{\"switch:0\":{\"id\":0}}")]
    [InlineData("{\"switch:0\":true}")]
    [InlineData("{\"switch:\":{\"output\":true}}")]
    [InlineData("{\"switch:x\":{\"output\":true}}")]
    [InlineData("{\"switch:00\":{\"output\":true}}")]
    [InlineData("{\"switch:-1\":{\"output\":true}}")]
    [InlineData("{\"switch:99\":{\"output\":true}}")]
    [InlineData("{\"switch:123\":{\"output\":true}}")]
    [InlineData("{\"switch:0\":{\"output\":true},\"switch:0\":{\"output\":false}}")]
    [InlineData("[]")]
    public void Should_RefuseSwitchesItCantRead(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadSwitches(document.RootElement));
    }

    [Theory]
    [InlineData("{}", 0, 0u)]
    [InlineData("{\"cover:0\":{\"id\":0}}", 0, 0u)]
    [InlineData("{\"switch:1\":{\"output\":true},\"switch:0\":{\"output\":false}}", 2, 0b10u)]
    [InlineData("{\"switch:0\":{\"output\":true},\"switch:1\":{\"output\":true},\"switch:2\":{\"output\":true}}", 3, 0b111u)]
    public void Should_CountTheSwitches(string json, int count, uint outputs)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(new ShellySwitches(count, outputs), ShellyJson.ReadSwitches(document.RootElement));
    }

    [Theory]
    [InlineData("{\"mac\":\"x\"}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"a\\\"b\"}")]
    [InlineData("{\"id\":5}")]
    [InlineData("[]")]
    public void Should_RefuseDeviceInfoWithoutAPlainId(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadDeviceInfo(document.RootElement));
    }

    [Fact]
    public void Should_ReadDeviceInfo_WithoutAGeneration()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"id\":\"shelly1-abc\",\"gen\":\"2\",\"profile\":\"cover\"}");

        Assert.Equal(new ShellyDeviceInfo("shelly1-abc", null, null, null, "cover"), ShellyJson.ReadDeviceInfo(document.RootElement));
    }

    [Theory]
    [InlineData("1.7.0", "1.7.0")]
    [InlineData("1.7.1", "1.7.1")]
    [InlineData("2.0.0-beta1", "2.0.0")]
    [InlineData("0.6.7", "0.6.7")]
    [InlineData("1.7", "1.7.0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("v1.7.0", null)]
    [InlineData("1.7.0.1", null)]
    [InlineData("1..0", null)]
    [InlineData("99999999999.0.0", null)]
    public void Should_ReadTheFirmwareVersion(string? ver, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), ShellyApi.ParseFirmware(ver));
    }

    [Fact]
    public void Should_ShowOnlyPrintableText_FromTheDevice()
    {
        var error = new ShellyRpcError(-109, "Over\u0000temp\r\n" + new string('x', 300));

        Assert.StartsWith("Over·temp··x", error.ShownMessage, StringComparison.Ordinal);
        Assert.EndsWith("…", error.ShownMessage, StringComparison.Ordinal);
        Assert.Equal(201, error.ShownMessage.Length);
    }

    [Fact]
    public void Should_ReadARequest_ForTheSimulator()
    {
        var request = ShellyJson.TryReadRequest(ShellyJson.WriteSwitchSet(3, relay: 1, on: false))!;

        Assert.Equal(("3", "contactor", ShellyApi.SwitchSet), (request.Id, request.Source, request.Method));
        Assert.Equal(0, request.Params!.Value.GetProperty("id").GetInt32());
        Assert.Null(ShellyJson.TryReadRequest("{\"id\":1}"u8));
        Assert.Null(ShellyJson.TryReadRequest("{\"id\":1,\"method\":\"x\",\"params\":[]}"u8));
        Assert.Null(ShellyJson.TryReadRequest("{\"id\":{},\"method\":\"x\"}"u8));
        Assert.Null(ShellyJson.TryReadRequest("nope"u8));
    }
}
