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
| Components and Services → *Switch* | `Switch.Set` (`id` and `on`, and its result `was_on`) and the switch status (`output`) |
| *Changelog* | 2.0.0 (2026-07-13): "Authentication: Implement brute-force protection" and "RFC 7616-compliant nonce management" |

Also RFC 7616, *HTTP Digest Access Authentication*, section 3.4.1 (the response with qop "auth") and section 3.9.1
(the SHA-256 example the tests use).

## Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `shelly-1` | Shelly Gen2+ with 1 relay | 1 | 0 | none | optional password |
| `shelly-2` | Shelly Gen2+ with 2 relays | 2 | 0 | none | optional password |
| `shelly-3` | Shelly Gen2+ with 3 relays | 3 | 0 | none | optional password |
| `shelly-4` | Shelly Gen2+ with 4 relays | 4 | 0 | none | optional password |

The docs list no model codes for the app to tell the devices apart, so the catalogue has one entry per number of
relays (`ShellyModels`). The device must have that many `switch:N` components, or the app refuses it.

- Port 80.
- The device's own schedules, auto-on and auto-off can switch its relays. The app can't see them yet:
  `OwnRules: OwnRulesAbility.CantTell`.
- The app doesn't read the device's inputs, power metering or `errors` list, and doesn't use the WebSocket.

## Protocol

Every call is one HTTP request: `POST /rpc` with a request frame as the body, `Content-Type: application/json`. The
answer is a response frame. The device closes the connection after the answer. It never sends anything by itself over
HTTP, so the app polls it every 2 s.

| What | Request frame | Result |
|---|---|---|
| Which device it is | `{"id":1,"src":"contactor","method":"Shelly.GetDeviceInfo"}` | `{"id":"shellypro4pm-f008d1d8b8b8","model":"SPSW-004PE16EU","gen":2,"ver":"0.6.7",…}` |
| Read the relays | `{"id":2,"src":"contactor","method":"Shelly.GetStatus"}` | `{"switch:0":{"id":0,"output":true,…},"switch:1":{…},…}` |
| Switch one relay | `{"id":3,"src":"contactor","method":"Switch.Set","params":{"id":1,"on":true}}` | `{"was_on":false}` |

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

`ShellyJsonTests` reads the docs' own examples, byte for byte: the `Shelly.GetStatus` result of a Pro 4PM, the
`/shelly` answer, the `Switch.Set` answers of "RPC Channels" and "RPC Protocol", and the error frames of "RPC
Protocol" (-105) and "Common Errors" (-109).

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
  after its own wait, 1 s doubling to 30 s.
- At 30,000 requests on a nonce the app asks for a new one itself.

The app checks a challenge before it answers: a `Digest` challenge with `algorithm=SHA-256` and `qop` "auth". The
realm and the nonce must be printable ASCII without a quote or a backslash, and at most 256 characters. Any other
challenge is an answer the app can't read.

The docs give no limit for the password. The app takes at most 128 characters, its own limit. Any character works,
because the password only goes into the hash. The app never logs or traces the password or the `Authorization`
header. The Debug screen says "· with the password's digest" after a request that had one.

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

- `WrongModel` for another number of relays, with the entry for the count found (`ShellyModels.ModelFor`), or null
  for a count the catalogue has no entry for. Also `WrongModel`, with no model found, for a Gen1 device and for the
  cover profile.
- `LoginNeeded`: the device asks for a password and the app has none.
- `LoginRefused`: the device refused the password.
- `FirmwareTooOld`, with 1.7.0 as `MinFirmware`, for firmware that is too old or that the app can't read.
- `TooManyTries` for a 429: the screens say that there were too many wrong passwords, to check the password and to
  wait. The poller tries again after its own wait.

`ShellyWrongDeviceException.Mismatch` tells the four refusals of the device apart.

A command (ON or OFF) switches the relays in the mask one at a time:

- The client sends `Switch.Set` for every relay in the mask, also when the device refuses one.
- A refused relay shows the device's own message, for example "The Shelly didn't take the command: relay 2:
  Overtemp condition present (error -109)".
- The whole command goes to the address it started on. When the address moves, or the device counts as lost, while
  it runs, nothing more is sent: the command throws `BoardNotConnectedException`. A kept nonce only goes back to the
  address that gave it.
- A request with no answer stops the rest of the mask: the device is gone. A 401 or a 429 also stops it: every other
  relay would get the same, and each try counts toward the device's brute-force protection.
- When the device takes no relay, the command throws. Otherwise it returns the relays the device took.
- When the device refused every relay with an error frame, it answered: that is a refusal (`BoardRefusedException`),
  which does not count toward losing the device. Anything else counts as a failed poll.
- After the switches, the client reads `Shelly.GetStatus` in the same command, and the poller takes that as the new
  state. When that read fails, the client keeps the last state and reads again at once.

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
  shows them. Any other method gets the error 404 "No handler for …", which isn't from the docs: the app sends no
  other method.
- With a password it asks for the digest, in the way of the firmware it is set to (`Firmware`, "2.0.0" by default):
  - 2.0.0 and later: a base64 nonce for many requests, `nc` strictly higher each time, `stale=true` after 1 hour or
    30,000 requests (on its `TimeProvider`), 32 nonces at most, and 429 after more than 10 wrong passwords in 10
    minutes, with the docs' waits.
  - Before 2.0.0: a numeric nonce for one request.
- `Generation` (null for a Gen1 device, which answers 404), `Firmware` and `Profile` make a device the app must
  refuse. `FailRelays` and `Refusal` make `Switch.Set` fail, by default with -109 "Overtemp condition present".
  `Throttle` answers 429. `ExpireNonces` and `ForgetNonces` make the next kept nonce fail.
- The simulator's page (`Contactor.Simulator`) offers port 8180 for it, because port 80 needs rights a developer
  doesn't have.
- Relays can be switched "at the device", and connections can be dropped or refused. A request that is too long,
  cut short or not HTTP is closed without an answer. A request that isn't a request frame gets 400 (the docs don't
  say what the device does).

## To check on a real board

> **Warning: check the first two points first, before anything is connected to the relays.**

- That `switch:0` is the relay marked O1 (or O) on the device, and so on.
- The relay states after a power cut, with each `initial_state` (Issue #516).
- Both firmware lines: a Plus device on 1.7.x (a challenge for every request) and a Pro or Gen3/Gen4 device on 2.x
  (one nonce, a growing `nc`, `stale=true` after an hour).
- What a refused `Switch.Set` answers: the -109 frame of "Common Errors", its HTTP status, and its message, for
  example with an overtemperature or overpower condition.
- The HTTP status of an error frame for `POST /rpc`. The docs show 200 only.
- If the device takes `src` in a request frame over HTTP. The docs' HTTP examples leave it out.
- What the device answers for a wrong password, also after 10 of them (429), and if a 429 has a body.
- If the device takes a kept nonce for `Switch.Set` as well as for `Shelly.GetStatus`.
- That a 2PM in the cover profile answers `profile` "cover", and a 2PM in the switch profile 2 switches.
- What a Gen1 device answers to `POST /rpc`. The app expects 404.
- The longest password the device's web page takes, and if a password with non-ASCII letters works.
- How the device handles a poll every 2 s for days, with and without a password.
- How many HTTP clients the device takes at once. The docs say 6 non-persistent channels. If too many clients also
  get 429, the screens say "Too many wrong passwords" for that too.
