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

        // sys.cfg_rev is 26 and sys.schedule_rev 0 in the docs' example, and input:0 to input:3 have the state false.
        var switches = ShellyJson.ReadSwitches(document.RootElement);
        Assert.Equal(new ShellySwitches(4, 0b0001, 26, 0, Inputs: 0b1111, InputsOn: 0, InputsPresent: 0b1111), switches);
        Assert.Equal(4, switches.InputCount);
    }

    // "Input", "Status": state "boolean or null (only for type switch, button)", null "for type button", and null when
    // the input isn't enabled; percent for type analog, counts for type count. The Input.GetStatus example's
    // {"id":0,"state":false} first. Input 2 is input:1. Every input:0 to input:3 counts, whatever its type (Issue #623).
    // Only one with a state is read.
    [Theory]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":false}}", 0b1, 0b0, 1)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":true},\"input:1\":{\"id\":1,\"state\":true}}", 0b11, 0b11, 2)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":null},\"input:1\":{\"id\":1,\"state\":true}}", 0b10, 0b10, 2)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":true},\"input:1\":{\"id\":1,\"percent\":42.5}}", 0b01, 0b01, 2)]
    [InlineData("{\"input:0\":{\"id\":0,\"counts\":{\"total\":3},\"freq\":0}}", 0, 0, 1)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":true},\"input:1\":{\"id\":1,\"state\":null},\"input:2\":{\"id\":2,\"percent\":7},\"input:3\":{\"id\":3,\"counts\":{\"total\":1}},\"input:100\":{\"id\":100,\"state\":true}}", 0b0001, 0b0001, 4)]
    [InlineData("{\"input:3\":{\"id\":3,\"state\":true},\"input:4\":{\"id\":4,\"state\":true},\"input:100\":{\"id\":100,\"state\":true}}", 0b1000, 0b1000, 0)]
    [InlineData("{\"input:00\":{\"state\":true},\"input:-1\":{\"state\":true},\"input:\":{\"state\":true},\"input:1\":{\"id\":0,\"state\":true}}", 0, 0, 0)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":\"on\"},\"input:1\":{\"id\":\"1\",\"state\":true},\"input:2\":[true]}", 0, 0, 3)]
    [InlineData("{\"input:0\":{\"id\":0,\"state\":true},\"input:0\":{\"id\":0,\"state\":false}}", 0, 0, 1)]
    [InlineData("{\"input:0\":{\"state\":true}}", 0b1, 0b1, 1)]
    public void Should_CountEveryInput_AndReadOnlyOnesWithAState(string json, int inputs, int on, int count)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        // Never refused: an input the app can't read is counted but not read, and the switches are still read.
        var switches = ShellyJson.ReadSwitches(document.RootElement);
        Assert.Equal((byte)inputs, switches.Inputs);
        Assert.Equal((byte)on, switches.InputsOn);
        Assert.Equal(count, switches.InputCount);
        Assert.Equal(0, switches.Count);
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

    // "Shelly", "Shelly.ListMethods example", the HTTP GET response, byte for byte (a Pro 4PM).
    private const string ListMethodsExample = """
        {
          "methods": [
            "Switch.SetConfig",
            "Switch.GetConfig",
            "Switch.GetStatus",
            "Switch.Toggle",
            "Switch.Set",
            "Schedule.List",
            "Schedule.DeleteAll",
            "Schedule.Delete",
            "Schedule.Update",
            "Schedule.Create",
            "Input.SetConfig",
            "Input.GetConfig",
            "Input.GetStatus",
            "Webhook.ListSupported",
            "Webhook.List",
            "Webhook.DeleteAll",
            "Webhook.Delete",
            "Webhook.Update",
            "Webhook.Create",
            "Mqtt.SetConfig",
            "Mqtt.GetConfig",
            "Mqtt.GetStatus",
            "Cloud.SetConfig",
            "Cloud.GetConfig",
            "Cloud.GetStatus",
            "BLE.SetConfig",
            "BLE.GetConfig",
            "BLE.GetStatus",
            "Eth.SetConfig",
            "Eth.GetConfig",
            "Eth.GetStatus",
            "Wifi.Scan",
            "Wifi.SetConfig",
            "Wifi.GetConfig",
            "Wifi.GetStatus",
            "Sys.SetConfig",
            "Sys.GetConfig",
            "Sys.GetStatus",
            "HTTP.GET",
            "Shelly.ListMethods",
            "Shelly.PutTLSClientKey",
            "Shelly.PutTLSClientCert",
            "Shelly.PutUserCA",
            "Shelly.PutHTTPServerCert",
            "Shelly.PutHTTPServerKey",
            "Shelly.PutHTTPServerCABundle",
            "Shelly.Reboot",
            "Shelly.SetAuth",
            "Shelly.Update",
            "Shelly.CheckForUpdate",
            "Shelly.DetectLocation",
            "Shelly.ListTimezones",
            "Shelly.GetStatus",
            "Shelly.FactoryReset",
            "Shelly.ResetWiFiConfig",
            "Shelly.GetConfig",
            "Shelly.GetDeviceInfo",
            "Shelly.ListProfiles",
            "Shelly.SetProfile"
          ]
        }
        """;

    [Fact]
    public void Should_ReadTheDocsListMethods_WithTheScheduleList()
    {
        using var document = System.Text.Json.JsonDocument.Parse(ListMethodsExample);

        var methods = ShellyJson.ReadMethods(document.RootElement);

        Assert.Equal(59, methods.Count);
        Assert.Contains(ShellyApi.ScheduleList, methods);
        Assert.Contains(ShellyApi.ListMethods, methods);
        Assert.DoesNotContain("schedule.list", methods);
    }

    // Anything but an array of names is refused, and so is a list longer than the app reads.
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"methods":{}}""")]
    [InlineData("""{"methods":"Schedule.List"}""")]
    [InlineData("""{"methods":["Schedule.List",1]}""")]
    [InlineData("""{"methods":[null]}""")]
    [InlineData("""[]""")]
    public void Should_RefuseMethodsItCantRead(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadMethods(document.RootElement));
    }

    [Fact]
    public void Should_RefuseMoreMethodsThanTheAppReads()
    {
        static string Methods(int count)
        {
            return $$"""{"methods":[{{string.Join(",", Enumerable.Range(1, count).Select(i => $"\"M.{i}\""))}}]}""";
        }

        using (var most = System.Text.Json.JsonDocument.Parse(Methods(ShellyApi.MaxMethods)))
        {
            Assert.Equal(ShellyApi.MaxMethods, ShellyJson.ReadMethods(most.RootElement).Count);
        }

        using var tooMany = System.Text.Json.JsonDocument.Parse(Methods(ShellyApi.MaxMethods + 1));
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadMethods(tooMany.RootElement));
    }

    // "Schedule", "Schedule.List example", the HTTP GET response, byte for byte.
    private const string ScheduleListExample = """
        {
          "jobs": [
            {
              "id": 1,
              "enable": true,
              "timespec": "0 0 8 * * SUN,MON,TUE,WED,THU,FRI,SAT",
              "calls": [
                {
                  "method": "Switch.Set",
                  "params": {
                    "id": 0,
                    "on": false
                  }
                }
              ]
            },
            {
              "id": 2,
              "enable": true,
              "timespec": "0 30 19 * * MON,TUE,WED,THU,FRI",
              "calls": [
                {
                  "method": "Switch.Set",
                  "params": {
                    "id": 0,
                    "on": true
                  }
                }
              ]
            }
          ],
          "rev": 4
        }
        """;

    [Fact]
    public void Should_ReadTheDocsScheduleList_AsTwoJobsOnRelayOne()
    {
        using var document = System.Text.Json.JsonDocument.Parse(ScheduleListExample);

        var schedules = ShellyJson.ReadSchedules(document.RootElement, switchCount: 4);

        Assert.Equal([new ShellyJob(1, true, 0b0001), new ShellyJob(2, true, 0b0001)], schedules.Jobs);
        Assert.Equal(4, schedules.Revision);
        Assert.Equal(0b0001, schedules.Relays);
        Assert.Equal([1L, 2L], schedules.Switching(1).Select(j => j.Id));
        Assert.Empty(schedules.Switching(2));
    }

    [Theory]
    [InlineData("""{"method":"Switch.Set","params":{"id":2,"on":true}}""", 0b0100)]
    [InlineData("""{"method":"Switch.Set","params":{"id":0,"on":false,"toggle_after":60}}""", 0b0001)]
    [InlineData("""{"method":"Switch.Toggle","params":{"id":3}}""", 0b1000)]
    [InlineData("""{"method":"Switch.SetAll","params":{"on":true}}""", 0b1111)]
    [InlineData("""{"method":"Switch.SetAll"}""", 0b1111)]
    [InlineData("""{"method":"Switch.SetMany","params":{"ids":[0,3],"on":true}}""", 0b1001)]
    [InlineData("""{"method":"Switch.SetMany","params":{"ids":[1,4,-1,1.5,"2",null],"on":true}}""", 0b0010)]
    [InlineData("""{"method":"Switch.SetMany","params":{"ids":1,"on":true}}""", 0)]
    [InlineData("""{"method":"Switch.Set","params":{"id":4,"on":true}}""", 0)]
    [InlineData("""{"method":"Switch.Set","params":{"id":-1,"on":true}}""", 0)]
    [InlineData("""{"method":"Switch.Set","params":{"id":"0","on":true}}""", 0)]
    [InlineData("""{"method":"Switch.Set","params":{"id":99999999999,"on":true}}""", 0)]
    [InlineData("""{"method":"Switch.Set"}""", 0)]
    [InlineData("""{"method":"Switch.Set","params":[0]}""", 0)]
    [InlineData("""{"method":"switch.set","params":{"id":0,"on":true}}""", 0)]
    [InlineData("""{"method":"Shelly.Reboot"}""", 0)]
    [InlineData("""{"method":"Script.Start","params":{"id":0}}""", 0)]
    [InlineData("""{"method":7}""", 0)]
    [InlineData("""{"params":{"id":0}}""", 0)]
    [InlineData("""[]""", 0)]
    [InlineData("""null""", 0)]
    public void Should_ReadOnlyTheCallsThatSwitchARelay(string call, int relays)
    {
        using var document = System.Text.Json.JsonDocument.Parse($$"""{"jobs":[{"id":5,"enable":true,"timespec":"@sunset","calls":[{{call}}]}],"rev":1}""");

        Assert.Equal(new ShellyJob(5, true, (byte)relays), Assert.Single(ShellyJson.ReadSchedules(document.RootElement, switchCount: 4).Jobs));
    }

    [Fact]
    public void Should_JoinEveryCallOfAJob_AndLeaveDisabledJobsOutOfTheRelays()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"jobs":[
              {"id":1,"enable":false,"timespec":"0 0 8 * * *","calls":[{"method":"Switch.Set","params":{"id":0,"on":true}}]},
              {"id":2,"timespec":"0 0 9 * * *","calls":[{"method":"Switch.Set","params":{"id":1,"on":true}},{"method":"Shelly.Reboot"},{"method":"Switch.Toggle","params":{"id":2}}]},
              {"id":3,"enable":true,"timespec":"0 0 9 * * *","calls":[]}
            ]}
            """);

        var schedules = ShellyJson.ReadSchedules(document.RootElement, switchCount: 3);

        // "enable": "It is true by default"; a job without a call that switches a relay switches none.
        Assert.Equal([new ShellyJob(1, false, 0b001), new ShellyJob(2, true, 0b110), new ShellyJob(3, true, 0)], schedules.Jobs);
        Assert.Null(schedules.Revision);
        Assert.Equal(0b110, schedules.Relays);
        Assert.Empty(schedules.Switching(1));
        Assert.Equal([2L], schedules.Switching(3).Select(j => j.Id));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("""{"jobs":{}}""")]
    [InlineData("""{"jobs":[1]}""")]
    [InlineData("""{"jobs":[{"enable":true,"calls":[]}]}""")]
    [InlineData("""{"jobs":[{"id":"1","enable":true,"calls":[]}]}""")]
    [InlineData("""{"jobs":[{"id":1.5,"enable":true,"calls":[]}]}""")]
    [InlineData("""{"jobs":[{"id":1,"enable":"yes","calls":[]}]}""")]
    [InlineData("""{"jobs":[{"id":1,"enable":null,"calls":[]}]}""")]
    [InlineData("""{"jobs":[{"id":1,"enable":true}]}""")]
    [InlineData("""{"jobs":[{"id":1,"enable":true,"calls":{}}]}""")]
    [InlineData("""{"jobs":[{"id":1,"calls":[]},{"id":1,"calls":[]}]}""")]
    public void Should_RefuseSchedulesItCantRead(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadSchedules(document.RootElement, switchCount: 2));
    }

    // "There is a limit of 20 schedule instances per device" and "a limit of 5 calls per schedule job": more is refused.
    [Fact]
    public void Should_RefuseMoreJobsOrCallsThanTheDocsAllow()
    {
        static string Jobs(int jobs, int calls)
        {
            var call = """{"method":"Switch.Set","params":{"id":0,"on":true}}""";
            var job = string.Join(",", Enumerable.Range(1, jobs).Select(i => $$"""{"id":{{i}},"enable":true,"calls":[{{string.Join(",", Enumerable.Repeat(call, calls))}}]}"""));
            return $$"""{"jobs":[{{job}}],"rev":1}""";
        }

        using (var most = System.Text.Json.JsonDocument.Parse(Jobs(ShellyApi.MaxScheduleJobs, ShellyApi.MaxScheduleCalls)))
        {
            Assert.Equal(20, ShellyJson.ReadSchedules(most.RootElement, switchCount: 1).Jobs.Count);
        }

        using var tooManyJobs = System.Text.Json.JsonDocument.Parse(Jobs(ShellyApi.MaxScheduleJobs + 1, 1));
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadSchedules(tooManyJobs.RootElement, switchCount: 1));
        using var tooManyCalls = System.Text.Json.JsonDocument.Parse(Jobs(1, ShellyApi.MaxScheduleCalls + 1));
        Assert.Throws<ShellyProtocolException>(() => ShellyJson.ReadSchedules(tooManyCalls.RootElement, switchCount: 1));
    }

    [Fact]
    public void Should_ReadTheDocsGetConfig_AutoOnOff()
    {
        using var document = System.Text.Json.JsonDocument.Parse(GetConfigExample);

        Assert.False(ShellyJson.ReadAutoOn(document.RootElement));
    }

    [Theory]
    [InlineData("""{"id":0,"auto_on":true}""", true)]
    [InlineData("""{"id":0,"auto_on":false}""", false)]
    [InlineData("""{"id":0}""", null)]
    [InlineData("""{"id":0,"auto_on":"true"}""", null)]
    [InlineData("""{"id":0,"auto_on":1}""", null)]
    [InlineData("""[]""", null)]
    public void Should_ReadAutoOn_OnlyAsTrueOrFalse(string json, bool? autoOn)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(autoOn, ShellyJson.ReadAutoOn(document.RootElement));
    }

    [Theory]
    [InlineData("""{"id":0,"in_mode":"flip","in_locked":false}""", OwnRuleKind.InputEdge)]
    [InlineData("""{"id":0,"in_mode":"follow","in_locked":false}""", OwnRuleKind.InputEdge)]
    [InlineData("""{"id":0,"in_mode":"momentary","in_locked":false}""", OwnRuleKind.InputToggle)]
    [InlineData("""{"id":0,"in_mode":"activate"}""", OwnRuleKind.InputToggle)]
    [InlineData("""{"id":0,"in_mode":"detached","in_locked":false}""", null)]
    [InlineData("""{"id":0,"in_mode":"flip","in_locked":true}""", null)]
    [InlineData("""{"id":0,"in_mode":"momentary","in_locked":true}""", null)]
    [InlineData("""{"id":0}""", null)]
    public void Should_MapEachInputMode_ToItsRule(string json, OwnRuleKind? kind)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(new ShellyInputRule(kind), ShellyJson.ReadInputRule(document.RootElement));
    }

    // "cycle" steps both relays of a 2-relay device ("Cycle in_mode explanation"); locked, it switches nothing.
    [Fact]
    public void Should_ReadCycle_AsAToggleThatStepsBothRelays()
    {
        using var cycle = System.Text.Json.JsonDocument.Parse("""{"id":0,"in_mode":"cycle","in_locked":false}""");
        using var locked = System.Text.Json.JsonDocument.Parse("""{"id":0,"in_mode":"cycle","in_locked":true}""");

        Assert.Equal(new ShellyInputRule(OwnRuleKind.InputToggle, Cycle: true), ShellyJson.ReadInputRule(cycle.RootElement));
        Assert.Equal(ShellyInputRule.None, ShellyJson.ReadInputRule(locked.RootElement));
    }

    [Fact]
    public void Should_ReadTheDocsGetConfig_AsAnInputThatSwitchesAtEachChange()
    {
        using var document = System.Text.Json.JsonDocument.Parse(GetConfigExample);

        Assert.Equal(new ShellyInputRule(OwnRuleKind.InputEdge), ShellyJson.ReadInputRule(document.RootElement));
    }

    [Theory]
    [InlineData("""{"id":0,"in_mode":"toggle","in_locked":false}""")]
    [InlineData("""{"id":0,"in_mode":"Flip","in_locked":false}""")]
    [InlineData("""{"id":0,"in_mode":"","in_locked":false}""")]
    [InlineData("""{"id":0,"in_mode":1,"in_locked":false}""")]
    [InlineData("""{"id":0,"in_mode":null}""")]
    [InlineData("""{"id":0,"in_mode":"detached","in_locked":"false"}""")]
    [InlineData("""{"id":0,"in_mode":"flip","in_locked":null}""")]
    [InlineData("""[]""")]
    public void Should_ReadAnInputModeTheDocsDontName_AsUnknown(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Null(ShellyJson.ReadInputRule(document.RootElement));
    }

    [Fact]
    public void Should_WriteAnInputSetConfig_WithOnlyInMode()
    {
        Assert.Equal(
            """{"id":3,"src":"contactor","method":"Switch.SetConfig","params":{"id":2,"config":{"in_mode":"detached"}}}""",
            Encoding.UTF8.GetString(ShellyJson.WriteSwitchInputDetached(3, relay: 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchInputDetached(1, relay: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchInputDetached(1, relay: 5));
    }

    [Fact]
    public void Should_WriteTheDocsScheduleUpdate_AndAnAutoOnSetConfig_WithNothingElse()
    {
        // As the docs' Schedule.Update example: {"id":1,"method":"Schedule.Update","params":{"id":3,"enable":false}}.
        Assert.Equal(
            """{"id":1,"src":"contactor","method":"Schedule.Update","params":{"id":3,"enable":false}}""",
            Encoding.UTF8.GetString(ShellyJson.WriteScheduleOff(1, job: 3)));
        Assert.Equal(
            """{"id":2,"src":"contactor","method":"Switch.SetConfig","params":{"id":1,"config":{"auto_on":false}}}""",
            Encoding.UTF8.GetString(ShellyJson.WriteSwitchAutoOnOff(2, relay: 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchAutoOnOff(1, relay: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShellyJson.WriteSwitchAutoOnOff(1, relay: 5));
    }

    [Theory]
    [InlineData("""{"switch:0":{"output":true},"sys":{"schedule_rev":7}}""", 7L)]
    [InlineData("""{"switch:0":{"output":true},"sys":{}}""", null)]
    [InlineData("""{"switch:0":{"output":true},"sys":{"schedule_rev":"7"}}""", null)]
    [InlineData("""{"switch:0":{"output":true},"sys":{"schedule_rev":7.5}}""", null)]
    public void Should_ReadScheduleRev_OnlyAsAWholeNumber(string json, long? revision)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(revision, ShellyJson.ReadSwitches(document.RootElement).ScheduleRevision);
    }
}
