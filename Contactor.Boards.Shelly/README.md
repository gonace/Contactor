# Shelly

How the app talks to Shelly Gen2+ devices with 1 to 4 relays. What every board shares (`IRelayBoard`,
`IBoardConnection`, the board catalogue) is in `docs/ARCHITECTURE.md`.

This project has the client, the adapters for the app and a simulator. It has no web or database code.

Nobody has tested the app on a real Shelly yet. The list in "To check on a real board" shows what is not known.

## Sources

Shelly's *Gen2+ Device API* documentation, version 1.0, https://shelly-api-docs.shelly.cloud/gen2/, read 2026-11-18.
The pages, and what the app takes from each:

| Page | What the app takes |
|---|---|
| General → *RPC Protocol* | The request frame (`id`, `src`, `method`, `params`) and the response frame (`id`, `src`, `dst`, and `result` or `error`, never both) |
| General → *RPC Channels* | `POST /rpc` with the whole frame as the body, answered with a whole frame. HTTP has no keepalive and no notifications |
| General → *Authentication* | Digest with SHA-256 (RFC 7616), user `admin`, realm the device id. The 401 challenge in `WWW-Authenticate`. Nonce reuse with `nc`, `stale=true`, 1 hour and 30,000 requests a nonce, and 429 after wrong passwords (firmware 2.0.0 and later). A new challenge for every request before 2.0.0. `Shelly.GetDeviceInfo` needs no password |
| General → *Common Errors* | The error object (`code`, `message`), and -109 FAILED PRECONDITION, e.g. "Overtemp condition present" |
| General → *Firmware Update Policy* | The Plus devices (Plus 1, Plus 1PM, Plus 2PM, Plus Plug S …) stay on firmware 1.7.x |
| Components and Services → *Shelly* | `Shelly.GetDeviceInfo` (`id`, `model`, `gen`, `ver`, `profile`) and `Shelly.GetStatus` (a member per component, e.g. `switch:0`), with their examples |
| Components and Services → *Switch* | `Switch.Set` (`id` and `on`, and its result `was_on`) and the switch status (`output`). `Switch.GetConfig` (`id`) and `Switch.SetConfig` (`id` and `config`, and its result `restart_required`), with their examples. In the configuration, `initial_state`: "Output state to set on power_on. Range of values: off, on, restore_last, match_input", and `auto_on`: "True if the "Automatic ON" function is enabled, false otherwise". `Switch.Toggle` (`id`), `Switch.SetAll` (`on`) and `Switch.SetMany` (`ids` and `on`), only to read them in a schedule |
| Components and Services → *Schedule* | The job (`id`, `enable`, "true by default", `timespec`, `calls` with `method` and `params`), "a limit of 20 schedule instances per device" and "a limit of 5 calls per schedule job". `Schedule.List` (`jobs` and `rev`) and `Schedule.Update` (`id` and `enable`, and its result `rev`), with their examples |
| Components and Services → *Input* (read 2026-11-20) | The input's status: `state` "boolean or null (only for type switch, button)", "null if the input instance is stateless, i.e. for type button", `percent` for type `analog` and `counts` for type `count`. `enable`: "When disabled, the input instance ... reports status properties as null". Inputs are "identified with input:<id>". The `Input.GetStatus` example `{"id":0,"state":false}` |
| Devices → *Shelly Plus 1*, *Pro 1*, *Plus 2PM*, *Pro 3*, *Pro 4PM* (read 2026-11-20) | Their inputs: `input:0` on the Plus 1, `input:0` and `input:1` on the Pro 1 and Plus 2PM, `input:0` to `input:2` on the Pro 3, and `input:0` to `input:3` on the Pro 4PM |
| Components and Services → *Sys* | `cfg_rev` in the status: "Configuration revision. This number will be incremented for every configuration change of a device component. If the new config value is the same as the old one there will be no change of this property". `schedule_rev`: "Schedules revision number, present if schedules are enabled" |
| *Changelog* | 2.0.0 (2026-07-13): "Authentication: Implement brute-force protection" and "RFC 7616-compliant nonce management" |

Also RFC 7616, *HTTP Digest Access Authentication*, section 3.4.1 (the response with qop "auth") and section 3.9.1
(the SHA-256 example the tests use).

## Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `shelly-1` | Shelly Gen2+ 1 | 1 | from the device, up to 4 | none | optional password |
| `shelly-2` | Shelly Gen2+ 2 | 2 | from the device, up to 4 | none | optional password |
| `shelly-3` | Shelly Gen2+ 3 | 3 | from the device, up to 4 | none | optional password |
| `shelly-4` | Shelly Gen2+ 4 | 4 | from the device, up to 4 | none | optional password |

The docs list no model codes for the app to tell the devices apart, so the catalogue has one entry per number of
relays (`ShellyModels`). The device must have that many `switch:N` components, or the app refuses it.

Devices with the same number of relays have different inputs: the Pro 1 has 2, the Plus 1 has 1. So the device says
how many it has at each connection (`BoardCapabilities.InputCountFromBoard`), and each entry's `InputCount` is 4, the
most the app reads. See "The inputs".

- Port 80.
- The device's own schedules and auto-on can switch its relays. The app reads them and turns them off, and the device
  may have rules the app can't see (scripts, webhooks, MQTT, the cloud, auto-off and the inputs):
  `OwnRules: ShellyApi.OwnRules`, which `Reads`, `TurnsOff` and `MayHaveUnseen`. See "The device's own rules".
- Each relay has its own state after a power cut, its switch's `initial_state`: `PowerOn: ShellyApi.PowerOn`, all four
  states, per relay.
- The app doesn't read the device's power metering or `errors` list, and doesn't use the WebSocket.

## Protocol

Every call is one HTTP request: `POST /rpc` with a request frame as the body, `Content-Type: application/json`. The
answer is a response frame. The device closes the connection after the answer. It never sends anything by itself over
HTTP, so the app polls it every 2 s.

| What | Request frame | Result |
|---|---|---|
| Which device it is | `{"id":1,"src":"contactor","method":"Shelly.GetDeviceInfo"}` | `{"id":"shellypro4pm-f008d1d8b8b8","model":"SPSW-004PE16EU","gen":2,"ver":"0.6.7",…}` |
| Read the relays | `{"id":2,"src":"contactor","method":"Shelly.GetStatus"}` | `{"switch:0":{"id":0,"output":true,…},"switch:1":{…},…}` |
| Switch one relay | `{"id":3,"src":"contactor","method":"Switch.Set","params":{"id":1,"on":true}}` | `{"was_on":false}` |
| Read one relay's settings | `{"id":4,"src":"contactor","method":"Switch.GetConfig","params":{"id":1}}` | `{"id":1,"name":null,"in_mode":"follow","initial_state":"match_input",…}` |
| Set one relay's state after a power cut | `{"id":5,"src":"contactor","method":"Switch.SetConfig","params":{"id":1,"config":{"initial_state":"off"}}}` | `{"restart_required":false}` |
| Read the schedules | `{"id":6,"src":"contactor","method":"Schedule.List"}` | `{"jobs":[{"id":1,"enable":true,"timespec":"0 0 8 * * *","calls":[{"method":"Switch.Set","params":{"id":0,"on":false}}]}],"rev":4}` |
| Turn one schedule off | `{"id":7,"src":"contactor","method":"Schedule.Update","params":{"id":3,"enable":false}}` | `{"rev":11}` |
| Turn one relay's auto-on off | `{"id":8,"src":"contactor","method":"Switch.SetConfig","params":{"id":1,"config":{"auto_on":false}}}` | `{"restart_required":false}` |

- `switch:0` is relay 1. `Switch.Set`'s `id` is the relay number minus 1.
- The app switches one relay per `Switch.Set`. It never sends `Switch.SetAll`, `Switch.Toggle` or a method that
  switches many relays.
- `src` is required in a request frame ("RPC Protocol"). The docs' HTTP examples leave it out. The app always sends
  `contactor`.
- An error frame has `error` instead of `result`, for example `{"code":-109,"message":"Overtemp condition present"}`.
  The docs show it with HTTP 200 only. The app also reads an error frame that comes with another status.
- The app reads at most 64 kB of an answer. The docs' `Shelly.GetStatus` of a Pro 4PM is about 3 kB.
- The app reads `output` (`true` or `false`) of each `switch:N`. The ids must be 0 to the count minus 1, without a
  gap. Every other component is skipped.

`ShellyJsonTests` reads the docs' own examples, byte for byte: the `Shelly.GetStatus` result of a Pro 4PM (with
`cfg_rev` 26, and its four inputs with the state false), the input status of `Input.GetStatus`, the `/shelly` answer, the `Switch.Set` answers of "RPC Channels" and "RPC Protocol", the
`Switch.GetConfig` and `Switch.SetConfig` answers of "Switch", the `Schedule.List` answer of "Schedule", and the error
frames of "RPC Protocol" (-105) and
"Common Errors" (-109).

### The inputs

The app reads the inputs from the same `Shelly.GetStatus` it polls, with no other request. `input:0` is input 1.

- Every `input:N` the status has counts, whatever its type (Issue #623). `input:0` to `input:3` only: 4 is the app's
  own limit, as many as the Pro 4PM has. So the inputs of an add-on, such as the Plus Add-on's `input:100` and up, are
  left out. An input with another key (`input:01`) is left out too.
- How many inputs the device has is decided at each new connection, from the connect's status: every input that
  counts, from `input:0` without a gap. An input after a gap isn't shown, so an input keeps its number. Before the
  device answered, the board has no inputs. While disconnected, the last count is kept. The count changes only when
  the device itself is replaced, never with an input's setting.
- The app reads an input's state only when its status has a `state` that is true or false. The docs give one only for
  type `switch`. True is on (closed).
- An input whose state the app doesn't read at the connect is unknown for the whole connection
  (`ShellyStatus.UnknownInputs` and `NotSwitchInputs`, `BoardState.UnknownInputs` and `NotSwitchInputs`). The screens
  show "–" and "The input isn't set as a switch on the device". These are:
  - a `button`: its `state` is null;
  - a disabled input: its `state` is null;
  - an `analog` input (`percent`) and a `count` input (`counts`);
  - an input the app can't read: an `id` that isn't the key's, a `state` of another kind, or the same key twice.
- An input never fails the poll. The inputs only decide what the app shows and which input actions run.
- During a connection the count and what the app reads of each input stay as they were:
  - An input whose setting was changed at the device since the connect (a switch made a `button`, `analog` or
    `count`, or turned off, or another type made a switch) is unknown until the next connection. It stays unknown when
    it is as it was again before then. The app never shows an old or a new state for it: the screens show "–" and
    "The input's setting was changed on the device".
  - So a change of the device's settings never looks like an input that changed. The app logs nothing and runs no
    input action for an unknown input.
  - At the next connection, every input is read afresh. The app compares an input that was unknown with its last
    known state and logs a difference as noticed, which runs no action.
- An input that changed is a state change (`ShellyStatus.StateComparer`), as a relay is. The input actions take it as
  they do on every board.
- The status trace adds the inputs: "Status: relay 2 on · input 1 on", "Status: all off · inputs off", and
  "· input 1 unknown" for an unknown input.

### The state after a power cut

`initial_state` in a switch's configuration says what the relay does when the device gets power:

| `initial_state` | `PowerOnState` |
|---|---|
| `off` | `Off` |
| `restore_last` | `AsBefore` |
| `on` | `On` |
| `match_input` | `FollowsInput` |

- The app reads it with one `Switch.GetConfig` per switch: at each new connection, after the status, and in a poll
  only when `sys.cfg_rev` differs from the one the settings were read with. While `cfg_rev` can't tell (the status
  has none, or the last read failed for a switch), a poll reads them again once a minute
  (`ShellyClient.SettingsInterval`). It never reads the settings on every poll, and never in the read that confirms
  a `Switch.Set`, which a group's next step waits for: that read keeps the last settings.
- A settings read never fails the connect or the poll. Only `Shelly.GetStatus` decides if the device is there, so a
  broken settings read can't keep the app from turning a relay off.
  - A value the docs don't list, or none, is unknown for that relay (null). So is a switch whose `Switch.GetConfig`
    the device refuses with an error frame, or answers with something the app can't read. The next switch is still
    asked.
  - No answer, a password refused or a 429 stops the read. That switch and the ones after it are unknown.
- The settings reads are routine in the Debug trace. Only a state that differs from the last read gets a toast.
- The app writes it only when a person saves it, with one `Switch.SetConfig` for that switch and only `initial_state`
  in `config`. In the same command it then reads the status and every switch's settings back, also when `cfg_rev`
  didn't change. A refusal with an error frame is an answer (`BoardRefusedException`), not a lost device.

### Password

The device's login is a password. The digest user name is always `admin`, so the screens ask for the password only
(`BoardModel.PasswordOnly`).

1. A request without the password gets `401 Unauthorized` with
   `WWW-Authenticate: Digest qop="auth", realm="<device id>", nonce="…", algorithm=SHA-256`.
2. The same request goes again with
   `Authorization: Digest username="admin", realm="…", nonce="…", uri="/rpc", algorithm=SHA-256, response="…", qop=auth, nc=00000001, cnonce="…"`.
3. `response = SHA256(HA1:nonce:nc:cnonce:auth:HA2)`, with `HA1 = SHA256(admin:realm:password)` and
   `HA2 = SHA256(POST:/rpc)`, each as lower-case hex, the password as UTF-8.

The two firmware lines differ:

- **Before 2.0.0** (the Plus line stays on 1.7.x): "Each HTTP request required going through the full 401
  challenge-response cycle". The nonce is a number. The app sends each request without the password first, and
  again with the digest. So every call that needs the password is two requests.
- **2.0.0 and later**: one nonce works for up to 30,000 requests or 1 hour, with `nc` 1 higher each time. The app
  keeps the nonce from the first challenge and sends the next calls with it at once. When the nonce has expired or is
  used up, the device answers 401 with `stale=true` and a new nonce.

A request goes at most twice:

- When the device refuses a kept nonce (stale, or forgotten after a reboot), the app sends the request once more,
  with the new challenge that came with the refusal.
- A 401 to a request with a new challenge is a wrong password. The app stops there.
- A 429 ("Too Many Requests") is never tried again within a call. From 2.0.0 the device answers 429 for a while after
  wrong passwords: 10 s between attempts after 10 of them, and up to 5 minutes after 40. The poller tries again
  once after 10 minutes, longer than the device counts wrong passwords for, or at once on "Check again" or a new
  password (Issue #522).
- A refused password that never connected isn't tried again by the poller at all: it waits for a new password or a
  person's "Check again", so the app never keeps the device's brute-force protection going. A password that connected
  before is tried once every 10 minutes for 24 hours from the first refusal, then held the same way.
- At 30,000 requests on a nonce the app asks for a new one itself.

The app checks a challenge before it answers: a `Digest` challenge with `algorithm=SHA-256` and `qop` "auth". The
realm and the nonce must be printable ASCII without a quote or a backslash, and at most 256 characters. Any other
challenge is an answer the app can't read.

The docs give no limit for the password. The app takes at most 128 characters, its own limit. Any character works,
because the password only goes into the hash. The app never logs or traces the password or the `Authorization`
header. The Debug screen says "· with the password's digest" after a request that had one.

### The device's own rules

The app reads two kinds of rules on the device that switch its relays without the app:

- **Schedules:** `Schedule.List`, at each new connection after the settings, and in a poll only when
  `sys.schedule_rev` differs from the one the schedules were read with. While `schedule_rev` can't tell (the status
  has none, or the last read failed), a poll reads them again once a minute (`ShellyClient.SettingsInterval`). Never
  in the read that confirms a `Switch.Set`.
  - An enabled job is a `Timer` rule on each relay its calls switch: `Switch.Set` and `Switch.Toggle` with an `id`,
    `Switch.SetAll` (every relay) and `Switch.SetMany` with `ids`. A call the app can't read, an id the device hasn't
    and any other method don't count. A disabled job is no rule.
  - Each enabled job that switches a relay is one unit of `OwnRules`, with its relays' timers. So `GoesWith` says
    exactly which other relays' timers a turn-off takes: a job that switches relays 1 and 2 takes relay 2's timer
    with relay 1's, unless another job still switches relay 2.
  - The app refuses an answer with more than 20 jobs, a job with more than 5 calls, a job without a whole-number `id`,
    two jobs with the same `id`, or an `enable` that isn't true or false. `enable` missing is true, as the docs say.
- **Auto-on:** `auto_on` from the same `Switch.GetConfig` the app reads for `initial_state`. True is an `AutoOn` rule
  on that relay, its own unit.

The rules come in `ShellyStatus.OwnRules`, in relay order, a relay's timer before its auto-on. They are null (unknown)
before both were read, and when the last `Schedule.List` failed or a switch's `auto_on` couldn't be read. A failed read
never fails the poll or makes the device lost, and is tried again after a minute. When the settings read stopped for
no answer, a password refused or a 429, the schedules aren't asked for in the same read. They are kept while the
device is disconnected. A change of the rules alone comes as `OwnRulesChanged`, not `StateChanged`. The schedule
reads are routine in the Debug trace. Only a change in which relays the enabled jobs switch gets a toast.

`TurnOffOwnRulesAsync` turns rules off, only when a person presses "Turn off":

- A `Timer` rule: one `Schedule.Update {id, enable:false}` for every enabled job that switches its relay, as last
  read (the jobs the person saw). The app never deletes a job and never changes its calls or `timespec`.
  This turns off the whole job, so its other calls (e.g. `Script.Start` or `HTTP.GET`) stop too.
- An `AutoOn` rule: one `Switch.SetConfig` for that switch, with only `auto_on:false` in `config`.
- At once or `BoardNotConnectedException`, never queued. `ArgumentOutOfRangeException` for a relay the entry hasn't
  or another kind of rule, before anything is sent. A rule given twice is taken once.
- It returns the rules asked that the device took, in relay order. A timer is taken when every job behind it took its
  update. A timer that no job switches any more, as last read, is taken without a request. A refused request is an
  answer: the others still go. When the device took none, it throws `BoardNotConnectedException`. While the schedules
  aren't known, a timer sends nothing.
- In the same command, it reads the status and the schedules (after a timer) or every switch's settings (after an
  auto-on) back, and the poller takes that as the new state.

## The app's client

`ShellyClient` sends one request at a time. `BoardPoller` does the polls, the reconnects and the count of failures, as
for the VM204.

On each new connection, the client:

1. Sends `Shelly.GetDeviceInfo`, which needs no password. It refuses, in plain words:
   - a device without `/rpc` (404), or without a `gen` of 2 or more: "The device at this address isn't a Shelly of
     the second generation or later. A Gen1 Shelly can't be used."
   - a device with `profile` "cover": "The Shelly is set up to drive a cover, not relays. Set its profile to
     "switch" in its own web page."
   - a `ver` before 1.7.0, or one the app can't read: "The Shelly's firmware is too old. Update it to 1.7.0 or later
     in its own web page."
2. Sends `Shelly.GetStatus`, with the password when the device asks for one. It refuses a device with another number
   of switches than the chosen entry: "The Shelly at this address has 4 relays, not 2."

The device counts as connected only after a poll after that answered.

When the device refuses the connection, the state says why (`ShellyState.Problem`, a `ConnectionProblem`):

- `WrongModel` for another number of relays, with the count found (`FoundRelayCount`) and the entry for it
  (`ShellyModels.ModelFor`), or null for a count the catalogue has no entry for. Also `WrongModel`, with no model
  found, for a Gen1 device.
- `CoverProfile` for a device in the cover profile: the screens say to set its profile to "switch".
- `LoginNeeded`: the device asks for a password and the app has none.
- `LoginRefused`: the device refused the password.
- `FirmwareTooOld`, with 1.7.0 as `MinFirmware`, for firmware that is too old or that the app can't read.
- `AskedToWait` for a 429: the screens say that the device asks to wait, and to check the password if it was just
  changed. A 429 comes after wrong passwords or with too many clients, so the words don't say the password is
  wrong, and the login fields stay as they are. The poller tries again after 10 minutes, or on "Check again".

`ShellyWrongDeviceException.Mismatch` tells the four refusals of the device apart.

A command (ON or OFF) switches the relays in the mask one at a time:

- The client sends `Switch.Set` for every relay in the mask, also when the device refuses one.
- A refused relay shows the device's own message, for example "The Shelly didn't take the command: relay 2:
  Overtemp condition present (error -109)".
- The whole command goes to the address it started on. When the address moves, or the device counts as lost, while
  it runs, nothing more is sent: the command throws `BoardNotConnectedException`. A kept nonce only goes back to the
  address that gave it.
- A request with no answer stops the rest of the mask: the device is gone. A 401 or a 429 also stops it: every other
  relay would get the same, and each try counts toward the device's brute-force protection. When the device took
  some relays before that, the command returns those, and the client reads the device again at once. Otherwise the
  command throws `BoardNotConnectedException`.
- When the device takes no relay, the command throws. Otherwise it returns the relays the device took.
- When the device refused every relay with an error frame, it answered: that is a refusal (`BoardRefusedException`),
  which does not count toward losing the device. Anything else counts as a failed poll.
- After the switches, the client reads `Shelly.GetStatus` in the same command, and the poller takes that as the new
  state. When that read fails, the client keeps the last state and reads again at once.

`SetPowerOnAsync` goes like a command: at once or `BoardNotConnectedException`, to the address it started on, and
`ArgumentOutOfRangeException` for a relay or a state the entry hasn't, before anything is sent. A change of the
states after a power cut alone comes as `PowerOnChanged`, not `StateChanged` (`ShellyStatus.StateComparer`).

`ShellyBoard`, `ShellyConnection` and `ShellyTraffic` are the adapters for the app. `ShellyTraceText` puts each
request in plain words. The polls and the answers that change nothing are routine.

## Settings

The `Shelly` section applies to all four entries:

```json
"Shelly": {
  "Host": "",
  "Port": 80,
  "Password": "",
  "PollSeconds": 2
}
```

## The simulator

`ShellySimulator` simulates a device with 1 to 4 switches on a local port.

- It answers `POST /rpc` with `Shelly.GetDeviceInfo`, `Shelly.GetStatus` and `Switch.Set`, in the docs' format.
  `Switch.Set` gets -103 without `id` or `on`, and -105 "Bad id=N" for a switch it doesn't have, as "Common Errors"
  shows them.
- It holds an `initial_state` per switch ("off" at start) and answers `Switch.GetConfig` and `Switch.SetConfig` with
  the docs' members. `Switch.SetConfig` takes only `initial_state` and `auto_on`, and -103 "Invalid argument
  'initial_state'" for a value the docs don't list (the docs don't say what the device answers then). `sys.cfg_rev` starts at 10 and grows
  with each change, but not for a value set to what it was. `SetInitialState` sets one "at the device" (any text),
  `ChangeAnotherSetting` grows `cfg_rev` alone, and `FailConfigRelays` refuses both methods for those relays.
  `UnreadableConfigRelays` answers `Switch.GetConfig` with a frame cut short, `SilentConfigRelays` never answers it,
  and `ReportsConfigRevision` false leaves `cfg_rev` out of the status.
- It holds each switch's `auto_on` (false at start) in `Switch.GetConfig`, and `Switch.SetConfig` takes it (-103
  "Invalid argument 'auto_on'" for a value that isn't true or false). A change grows `cfg_rev`. `SetAutoOn` sets one
  "at the device".
- It holds schedule jobs and answers `Schedule.List` (`jobs`, `rev`) and `Schedule.Update` (only `enable` is taken,
  -103 without `id`, -105 "Bad id=N" for a job it hasn't). `sys.schedule_rev` starts at 0 and grows with every job
  added and every update. `AddSchedule` adds a job "at the device" with its calls as JSON, `SetScheduleEnabled` turns
  one on or off, `FailSchedules` refuses `Schedule.List`, `FailScheduleUpdates` refuses `Schedule.Update` for some
  jobs, `SilentScheduleUpdates` never answers it for some, and `ReportsScheduleRevision` false leaves `schedule_rev`
  out of the status.
- It has as many inputs as switches, as the devices the docs list: `input:0` on a 1-relay device up to `input:3` on a
  4-relay device, each of type `switch` and open at start. `SetInputAsync` closes or opens one, and the simulator's
  page has a checkbox per input. `SetInputType`, for the tests only (the page has no control for it), makes one a
  `button` (`state` null) or `analog` (`percent` 0 or 100) "at the device", which grows `cfg_rev`.
- Any other method gets the error 404 "No handler for …", which isn't from the docs: the app sends no
  other method.
- With a password it asks for the digest, in the way of the firmware it is set to (`Firmware`, "2.0.0" by default):
  - 2.0.0 and later: a base64 nonce for many requests, `nc` strictly higher each time, `stale=true` after 1 hour or
    30,000 requests (on its `TimeProvider`), 32 nonces at most, and 429 after more than 10 wrong passwords in 10
    minutes, with the docs' waits.
  - Before 2.0.0: a numeric nonce for one request.
- `Generation` (null for a Gen1 device, which answers 404), `Firmware` and `Profile` make a device the app must
  refuse. `FailRelays` and `Refusal` make `Switch.Set` fail, by default with -109 "Overtemp condition present".
  `Throttle` answers 429. `ExpireNonces` and `ForgetNonces` make the next kept nonce fail. `ChangePassword` changes
  the password while the app runs. `AfterSwitchSet` lets a test change the device between two relays of one command.
- `Schedules` gives the jobs it holds, for the simulator's page.
- The simulator's page (`Contactor.Simulator`) offers port 8180 for it, because port 80 needs rights a developer
  doesn't have. The page has controls for `Throttle` (only for a device with a password: only then
  does it ask for the digest, so only then can it answer 429), `Firmware` (1 to 32 letters, digits, '.', '-', '_' or
  '+'; without a password it only decides whether the app refuses it: before 1.7.0, or one the app can't read),
  `Profile` (none, "switch" or "cover"), `FailRelays`, `SetAutoOn` and `SetInitialState` (the four values the docs
  list) per relay, and `SetScheduleEnabled` per job. It adds a job with `AddSchedule` that switches one relay on or
  off every day at a time ("HH:mm"), at most 20, as the device holds.
- Relays can be switched "at the device", and connections can be dropped or refused. A request that is too long,
  cut short or not HTTP is closed without an answer. A request that isn't a request frame gets 400 (the docs don't
  say what the device does).
- A power cut (`ISimulatedBoardPower`): each relay comes back as its `initial_state` says (`off`, `on`, `restore_last`). `match_input` follows the switch's input, which the simulator doesn't link to its inputs, so it comes back off. `power_restore_delay_enable` isn't simulated. The device forgets its nonces, as after a reboot.
- A new password while it runs (`ChangePassword`, or `ChangeLogin` with the user name ignored): a kept nonce still answers, but the old password is wrong from then on.

## To check on a real board

> **Warning: check the first two points first, before anything is connected to the relays.**

- That `switch:0` is the relay marked O1 (or O) on the device, and so on.
- The relay states after a power cut, with each `initial_state`: `off`, `restore_last` (once with the relay on and
  once off), `on` and `match_input`. Also with `power_restore_delay_enable`, which keeps the output off for a while
  first.
- That `cfg_rev` grows when `initial_state` changes, through `Switch.SetConfig` and through the device's own web page,
  and that `Switch.SetConfig` answers `restart_required` false for it.
- Both firmware lines: a Plus device on 1.7.x (a challenge for every request) and a Pro or Gen3/Gen4 device on 2.x
  (one nonce, a growing `nc`, `stale=true` after an hour).
- What a refused `Switch.Set` answers: the -109 frame of "Common Errors", its HTTP status, and its message, for
  example with an overtemperature or overpower condition.
- The HTTP status of an error frame for `POST /rpc`. The docs show 200 only.
- If the device takes `src` in a request frame over HTTP. The docs' HTTP examples leave it out.
- What the device answers for a wrong password, also after 10 of them (429), and if a 429 has a body.
- That the device's own wait after wrong passwords runs out by itself while the app holds a refused password, so the
  right password then connects at once, and that one attempt every 10 minutes after a 429 never keeps it going.
- If the device takes a kept nonce for `Switch.Set` as well as for `Shelly.GetStatus`.
- That a 2PM in the cover profile answers `profile` "cover", and a 2PM in the switch profile 2 switches.
- What a Gen1 device answers to `POST /rpc`. The app expects 404.
- That `schedule_rev` grows when a schedule is made, changed, turned off or deleted, also on the device's own web page,
  and that `Schedule.Update {id, enable:false}` leaves the job's `timespec` and `calls` as they were.
- What `Schedule.List` answers on a device with no schedules, and if a device answers without `schedule_rev` or
  refuses `Schedule.List` ("present if schedules are enabled"). The app then shows the rules as unknown.
- What `Schedule.Update` answers for a job that no longer exists.
- That a schedule made in the Shelly app or the cloud is in `Schedule.List`, with its calls as `Switch.Set`.
- That `auto_on` turns the relay on again after `auto_on_delay`, that `Switch.SetConfig` with `auto_on:false` stops
  a delay already running, and that `cfg_rev` grows when `auto_on` changes.
- The longest password the device's web page takes, and if a password with non-ASCII letters works.
- How the device handles a poll every 2 s for days, with and without a password.
- That an input of type `switch` gives `state` true when its terminal is closed, and that `invert` turns it the other
  way.
- That `input:N` is the input marked SW, SW1 or S1 (N = 0) on the device, and so on.
- What the status gives for an input set to `button`, `analog` or turned off: the docs say `state` null for a button
  and a disabled input, and `percent` for an analog one.
- That a Plus 1 or a Gen3 device with one relay has `input:0` only, and a Pro 1 `input:0` and `input:1`.
- How a switch's `in_mode` ("Mode of the associated input": `momentary`, `follow`, `flip`, `detached` …, "Switch")
  switches the relay at the device by itself. The app doesn't read it, so an input may switch a relay without the
  app (`MayHaveUnseen`).
- How many HTTP clients the device takes at once. The docs say 6 non-persistent channels. If too many clients also
  get 429.
