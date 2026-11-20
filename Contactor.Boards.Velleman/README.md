# Velleman

How the app talks to the VM201, the VM204 and the VM208. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is in `docs/ARCHITECTURE.md`.

The app talks to the VM201 over its own TCP protocol, and to the VM204 and the VM208 over HTTP. The three boards have different protocols, so each has its own types, named after the model (`Vm201*`, `Vm204*`, `Vm208*`), and its own section below.

## Sources

- VM201: Velleman's protocol description, via https://github.com/tlrh314/misc/tree/main/vm201 (a Python reference implementation). Confirmed with a packet dump from the Whadda forum.
- VM204: Velleman's firmware, https://github.com/Velleman/VM204-Firmware, branch V1.1 (CycloneTCP); each route names its firmware file in `Vm204Api`. For the analog input also Velleman's *User Manual – Ethernet Relay Card VM204*, December 2016, https://cdn.velleman.eu/downloads/0/user/usermanual_vm204_en.pdf.
- VM208: Velleman's firmware, https://github.com/Velleman/VM208 (`src/Network/server_VM208.cpp`, ESPAsyncWebServer, ArduinoJson 5), and its library https://github.com/velleman/VM208INT; each route names its firmware file in `Vm208Api`. The schedules (branch `master`, commit `0bef335`, read 2026-11-18): `src/Network/server_VM208.cpp` lines 182–211 (`/stoppulse`, `/stoptimer`), 231–281 (`/getalarms`), 283–304 (`/setalarm`) and 522–572 (`/shedule_set`); `src/config_vm208.cpp` lines 93–136 (`loadAlarms`), 252–260 (`getShedule`), 438–470 (`writeAlarm`) and 837–840 (`setShedule`); `src/ChannelShedule.cpp` (the 14 alarms and when they switch); `src/main.cpp` `checkSheduler`; `src/PulseAndTimer.cpp` (pulses and timers: `timerTask` lines 8–105, `pulseTask` 107–204). The pulses and timers in the status (same commit, read 2026-11-20): `src/Network/server_VM208.cpp` lines 85–132 (`/relay` stops the relay's pulse and timer), 164–180 (`/pulse`), 213–229 (`/timer`), 762–841 and 843–914 (the two `sendIOState`, each with its own copy of the flags); `src/IO/IO.cpp` lines 90–95 (the flags' queues, one entry each); `include/PulseAndTimer.hpp` (`TimeParameters_t`: 16-bit id and time); `data/www/js/index.js` `sendPulse`, `sendTimer` (the page's values: ms and minutes, 1–60000) and `updateIO` (how the page reads `pulseActive`, `timerActive`); `data/alarms/0.json` (the factory schedule); `data/www/js/shedule.js` `updateShedule` and `applyShedule` (how the board's own page sends and reads them).

## VM201

### Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `vm201` | Velleman VM201 | 8 | 1 | 1 s to 99 h | user name and password, optional |

### Protocol

The VM201 talks its own TCP protocol on port 9760.

#### Packet format

```
<STX><LEN><CMD><data_1>...<data_n><CHECKSUM><ETX>
STX = 0x02
ETX = 0x03
LEN = total number of bytes in the packet, including STX and ETX
CHECKSUM = two's complement of the sum of all preceding bytes: (byte)(-(sum(STX..last data) % 256) & 0xFF)
```

Note: the comment header of the original document wrongly says STX=0x0F/ETX=0x04. **0x02/0x03 is right.**

Verified example: turn on relay 1 = `02 06 4F 01 A8 03` (0x02+0x06+0x4F+0x01 = 0x58 → 0x100−0x58 = 0xA8).

#### Commands

| Name | Byte | LEN | Direction | Data |
|---|---|---|---|---|
| CMD_AUTH | `'A'` | 5 | board → client | Login required |
| CMD_USERNAME | `'U'` | 14 | client → board | 9 bytes user name, zero padded (max 8 characters) |
| CMD_PASSWORD | `'W'` | 14 | client → board | 9 bytes password, zero padded (max 8 characters) |
| CMD_LOGGED_IN | `'L'` | 5 | board → client | Logged in |
| CMD_ACCESS_DENIED | `'X'` | 5 | board → client | Wrong login (followed by CMD_CLOSED) |
| CMD_CLOSED | `'C'` | 5 | both | Close the session |
| CMD_NAME | `'N'` | 22 | board → client | 1 byte channel (1–9, 9 = the input) + 16 bytes name |
| CMD_STATUS_REQ | `'R'` | 5 | client → board | Request status |
| CMD_STATUS | `'S'` | 8 | board → client | 3 bytes: outputs, timer, input |
| CMD_ON | `'O'` | 6 | client → board | Channel mask: 1 = turn on |
| CMD_OFF | `'F'` | 6 | client → board | Channel mask: 1 = turn off |
| CMD_TOGGLE | `'T'` | 6 | client → board | Channel mask: 1 = toggle |
| CMD_PULSE | `'P'` | 8 | client → board | Channel mask, time 1–99, unit `'s'`/`'m'`/`'h'` |
| CMD_UPDATE | `'V'` | 6 | client → board | Set all: 1 = on, 0 = off |
| CMD_TMR_ENA | `'E'` | 6 | client → board | Enable the board's timer programs (mask) |
| CMD_TMR_DIS | `'D'` | 6 | client → board | Disable the board's timer programs (mask): 1 = turn its timer off, 0 = leave it |
| CMD_TMR_TOGGLE | `'G'` | 6 | client → board | Toggle the board's timer programs (mask) |

**Channel mask:** bit 0 = channel 1 … bit 7 = channel 8.

**CMD_PULSE data:** the channel mask, then the time as a number 1–99, then its unit as an ASCII letter: `'s'` seconds,
`'m'` minutes or `'h'` hours. The board turns the channels on, and off again by itself after the time. Example: pulse
relay 2 for 2 s = `02 08 50 02 02 73 2F 03`.

**CMD_STATUS data:** byte 1 = outputs (one bit per channel, 1 = ON), byte 2 = the board's timers (1 = active), byte 3 = input (bit 0, 1 = ON).

#### Session flow

1. Connect over TCP to `host:9760`.
2. The board sends `CMD_LOGGED_IN` (no auth) **or** `CMD_AUTH`. On `CMD_AUTH`: send `CMD_USERNAME` and then `CMD_PASSWORD`. The answer is `CMD_LOGGED_IN`, or `CMD_ACCESS_DENIED` + `CMD_CLOSED`.
3. After login the board sends **9 `CMD_NAME`** (8 outputs + the input) and then **`CMD_STATUS`**.
4. From then on the board sends **`CMD_STATUS` on its own at every change**, including changes made through the board's web interface, its buttons or its own timers.
5. End with `CMD_CLOSED`.

#### Known quirks

- Channel names can contain garbage characters from the firmware. **The app ignores the board's names**; the names are stored in SQLite.
- An ON/OFF that changes nothing gets **no** `CMD_STATUS` reply. Never wait for a reply after a command. Rely on the pushed status and on a periodic `CMD_STATUS_REQ`.
- Read the stream with a real frame parser: read STX, LEN, then the remaining LEN−2 bytes. Never assume one `Read` returns exactly one packet: TCP can split and merge packets.
- Validate the checksum and ETX. Log and drop invalid packets, but don't disconnect.

### The app's client

- `Vm201PacketCodec`: `byte[] Encode(byte cmd, ReadOnlySpan<byte> data)` and an incremental decoder over a `PipeReader` or `Stream`.
- `Vm201Client`:
  - Holds one persistent connection. On failure: reconnect with exponential backoff (1 s → max 30 s). A refused login (`LoginRefused`) is not tried again in that loop, by the polled boards' rule (Issue #537): a login that never logged in since the last `SetEndpoint` or `SetLogin` is held, with no timer, until a new address or login, or a person's "Check again" (`TryAgain`, which does nothing while connected). A login that logged in before is tried every 10 minutes for 24 hours from the first refusal (`RefusedLoginPause`, `RefusedLoginRetries`, the times of `BoardPoller`), and then held. The trace says "Not trying again until a new login is saved or a person checks again". `LoginNeeded` keeps the backoff.
  - Exposes `Vm201State` (Outputs byte, TimerMask, Input, `IsConnected`, `LastContactUtc`) and an event or `IObservable` when the status changes.
  - Commands (`TurnOnAsync(mask)`, `TurnOffAsync(mask)`, `PulseAsync(mask, length)`, `RequestStatusAsync()`, `TurnOffTimersAsync(mask)`) are serialized through a `Channel<T>`, so only one writer is used at a time, in the order they were sent. A command cancelled before the writer takes it is not sent, and its caller gets `OperationCanceledException`. A cancel after that comes too late: the command goes out, and its caller gets the write's own result.
  - `PulseAsync` sends the whole mask in one CMD_PULSE. A length goes out exactly or not at all (`Vm201Pulse`): whole seconds up to 99 s, else whole minutes up to 99 min, else whole hours up to 99 h. Any other length, such as 100 s or 90.5 s, throws `ArgumentOutOfRangeException` before anything is sent.
  - Timeouts: connect 5 s, login 5 s.
  - When the board refuses the login, `Vm201State.Problem` says why (a `ConnectionProblem`): `LoginNeeded` when the board asked for a login (CMD_AUTH) and the app has none, else `LoginRefused`. It never holds the login. The client clears it at the next login, in `SetLogin`, `SetEndpoint` and `TryAgain`, and when a later connection gets no answer (the board is gone, so it no longer says why); a refusal from a connection that the app ended meanwhile is dropped.
  - `SetEndpoint` and `SetLogin` change the address and login while it runs: the current connection, attempt or wait ends at once. The web app applies the address and login saved on its `Board` row before the first connection. `Vm201Login` is a class, not a record, so its password can't leak through a generated `ToString`.
  - The `Traced` event reports every packet in and out and every connection step (connecting, lost and why, the wait before the next attempt, skipped bytes) as a `Vm201TraceEntry`. User name and password packets come cut to their first three bytes. The web app keeps the latest 100 in memory (`BoardTrace`) for the Debug screen; the keep-alive and the channel names are routine, so it only counts them.
- `Vm201Board`: the VM201 as an `IRelayBoard` (8 relays, 1 input, reports changes, a pulse of 1 s to 99 h in steps of 1 s); it translates `Vm201State` and refuses relays above 8. Its own rules (`BoardState.OwnRules`) are its timer programs: a `Timer` rule per bit of the status's byte 2, in relay order; null before its first status (also at a new address), and kept while disconnected. The client raises its state for every status, the keep-alive's too, so a change of the timers alone comes with `StateChanged`, and the board never raises `OwnRulesChanged`. `TurnOffOwnRulesAsync` takes only `Timer` rules for relays 1–8 (anything else is `ArgumentOutOfRangeException`, before anything is sent), takes a rule given twice once, and sends one `CMD_TMR_DIS` with only their relays' bits: relays 3 and 5 are `02 06 44 14 A0 03`, never `0xFF`. It goes like any other command, at once or `BoardNotConnectedException`, never queued, and returns the rules sent; for none it sends nothing. Its `PulseAsync` refuses a length the board can't send exactly with `ArgumentOutOfRangeException`. Its pulse ability (`Vm201Pulse.Ability`) checks each length with `TryEncode` (`PulseAbility.Exact`), so `CanDo` also refuses such a length: `CanDo(100 s)` is false.
- `Vm201Connection` and `Vm201Traffic`: the client as an `IBoardConnection` and an `IBoardTraffic` (`Vm201TraceText` puts each packet in plain words). `Vm201Model` is its catalogue entry.

### Settings

`Vm201:LogPackets` (off by default) logs every packet in and out as hex with its command letter (`Vm201PacketTrace`); user name and password packets only as their command and length. The board keeps no log of its own, so this is how to see what a real board does.

### The simulator

- `Vm201Simulator`: a `TcpListener`-based fake that implements the whole protocol, optional auth, push on change, a pulse that ends by itself on its `TimeProvider` and a way to simulate a disconnect, as an `ISimulatedBoard` and `ISimulatedBoardTimers`. Its traffic uses the same words as the app's Debug screen (`Vm201TraceDescriber`). It is used in the tests and in development, where `Contactor.Simulator` runs it as its own process through Aspire. The web app has no built-in simulator: it always connects to `Vm201:Host`.
- "Run now" for a timer program (`ISimulatedBoardRules`): Velleman's description doesn't say what a timer program switches. The simulator assumes it turns its relay on at one time and off at another, so it offers both.
- A power cut (`ISimulatedBoardPower`): Velleman's description doesn't say how the relays come back (see "To check on a real board"), so the simulator brings them all back off. It assumes the timer programs stay, which is unknown too.
- A new login while it runs (`ChangeLogin`), as on the board's web page: a session that is logged in stays in; the next login needs the new one. The description doesn't say whether the board ends its sessions then; the simulator assumes it doesn't.

### Settings on the board that switch relays without the app

- **The board's own timer programs** switch relays without the app, so they compete with the app's schedules. `CMD_STATUS` shows which are active (byte 2), and `CMD_TMR_ENA`, `CMD_TMR_DIS` and `CMD_TMR_TOGGLE` turn them on and off by channel mask: only the channels whose bit is 1, the others are left as they are. The app reports them as the board's own rules (`Vm201Board`).
- **What the app does:** it never changes the timer programs on its own accord. It shows the ones it finds, warns, and turns them off only when a person presses "Turn them off", with one `CMD_TMR_DIS` holding only the chosen relays' bits (see `docs/ARCHITECTURE.md`, "The board's own rules"). Until issue #497 it sent `CMD_TMR_DIS 0xFF` at the first connection without asking (`Vm201:DisableBoardTimers`); that setting is gone, and one left in an installation's settings is ignored.
- **What the app sees:** the board pushes `CMD_STATUS` at every change, also one made by a timer program. The app logs a change it didn't ask for with Actor `external`, and a group's interlock checks every status.
- **What to set:** make the schedules in the app, not on the board, and turn the board's timer programs off when the app asks.

## VM204

The VM204 is not the VM201's protocol.

### Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `vm204` | Velleman VM204 | 4 | 4, and 1 analog input | none | user name and password, can be empty |

### Protocol

It is controlled only through its web server: plain HTTP, port 80 unless changed on the board, JSON answers, one request per connection (`Connection: close`), at most 3 connections. It never reports a change by itself, so the app polls.

- `GET /api/auth?user=U&password=P` → `{"ApiKey":"…"}` (32 letters and digits, kept by the board until someone makes a new one on its web page); a wrong login → 400. Factory login `admin` / `VM204`.
- `GET /api/status?key=K` → `{"relays": [b,b,b,b],"pulse": [b,b,b,b],"inputs": [b,b,b,b],"analog": n}`; a wrong key → 400.
- `GET /api/relay/on?key=K&relay=1,3` and `/api/relay/off?…` → switch the listed relays (1–4) and answer the status; a wrong key → 400.
- The analog input, `"analog": n` in every status (see [The analog input](#the-analog-input)).
- `/api/*` needs no HTTP basic auth; the board's own pages do. The firmware doesn't percent-decode the query and copies it into small buffers, so a user name or password is at most 31 characters, 48 together, of letters, digits and `- . _ ~ ! $ ' ( ) * + , ; : @` (`Vm204Login`).

### The analog input

| What | Value | Source |
|---|---|---|
| Count | 1 ("4 relay contacts, 4 digital inputs and 1 analog input") | manual p. 3 |
| Read | `"analog": n` in `GET /api/status`, n a whole number printed with `itoa` | firmware `Main/main.c` `createJsonData` |
| Resolution | 10 bit, 0–1023: the PIC32's converter on RB2/AN2 | firmware `Main/analog.c`; manual p. 19 ("between 1 and 1024") |
| Full scale | 1023 = 6.6 V at the terminal: the firmware converts `n / 1023 × 3.3 × 2`, and the board's own page shows that | firmware `Main/analog.c`, `resources/www/js/vm204.js` `updateIO`; manual p. 19 ("The maximum voltage that can be sensed is 6.6 V") |

- 1023 or 1024: the manual (p. 19) says 6.6 V "=1024" and "multiply your voltage value times 155", while the value tops out at 1023 and the firmware divides by 1023. The app follows the firmware, as the board's own page does: 1023 = 6.6 V, 6.6/1023 V per count. The two differ by one count.
- Not in Velleman's documents: the divider's resistors, the input's impedance and its absolute maximum. The manual's "Do not connect external voltage to the inputs" (pp. 2 and 11) may or may not cover the analog input.
- At 1023 the voltage may be higher than 6.6 V: `AnalogAbility.IsFullScale` says so. An answer without `analog`, or with a value outside 0–1023, has no reading for it (null in `BoardState.Analog`), but is otherwise read as usual.

### The app's client

- `Vm204Client`: `HttpClient` (no proxy, answers capped at 4 KiB, 5 s per request on `TimeProvider`), one request at a time; logs in for the key and polls through `BoardPoller` (`Vm204:PollSeconds`, default 2). A refused key is dropped and the next poll, asked for at once, logs in again. When the board answers 400 to `/auth` at the connect, `Vm204State.Problem` says why (a `ConnectionProblem`): `LoginNeeded` when the app has no login, else `LoginRefused`; a refused key is no problem. After `LoginRefused` for a login that never connected, the poller doesn't log in again by itself: it waits for a new login or a person's "Check again" (Issue #522). A login that connected before is tried every 10 minutes for 24 hours, then held the same way. It never holds the login, and `BoardPoller` clears it at the first answer, at a new address and at a new login. Commands throw `BoardNotConnectedException` while disconnected, never wait behind a poll, and their answer (the status) is the confirmation. A change of the analog value alone isn't a state change: `BoardPoller`'s comparer (`Vm204Status.SwitchesComparer`) leaves it out, and it comes as the client's `AnalogChanged` instead of `StateChanged`. Its `Traced` event gives each request and answer in plain words (`Vm204TraceText`): the key, the user name and the password never show, and the polls and unchanged answers are routine.
- `Vm204Json` reads the answers strictly (anything else is a `Vm204ProtocolException` and counts as a failed poll). `Vm204Board` (4 relays, 4 inputs, 1 analog input, doesn't report changes; the analog value in `BoardState.Analog`, and a change of it alone as `AnalogChanged`), `Vm204Connection`, `Vm204Traffic` and `Vm204Model` (id `vm204`, port 80) are its adapters, `Features/Vm204` registers them.

### The simulator

- `Vm204Simulator`: a small HTTP server on `TcpListener` with the same routes and 400s as the firmware, an optional login, relays and inputs set at the board, the analog value (0–1023, `SetAnalogAsync`, a slider on the simulator's page), a new key, and refused or dropped connections; a connection that sends no whole request head within 5 s is closed. The simulator's page offers port 8204 for a new one (`SimulatorModel.SuggestedPort`), as 80 needs rights a developer doesn't have.
- A power cut (`ISimulatedBoardPower`): the firmware doesn't say how the relays come back (see "To check on a real board"), so the simulator brings them all back off.
- A new login while it runs (`ChangeLogin`): `/api/auth` takes only the new one. The key the app has keeps working until it is made anew (`RegenerateKey`); the firmware doesn't say whether a new login makes a new key, so the simulator assumes it doesn't.

## VM208

The VM208 (ESP32, WLAN or Ethernet) is neither the VM201's nor the VM204's protocol.

### Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `vm208` | Velleman VM208 | 4 | 1 | none | none |

Only the card itself is used (`interface=-1`); VM208EX extension modules are left out for now.

### Protocol

Plain HTTP on port 80, JSON answers, one request per connection. It never reports a change by itself, so the app polls.

- `GET /status?interface=-1&socket=0` → `{"Channels":[{"name":"…","state":b,"pulseActive":b,"timerActive":b} ×4],"m1":b,"m2":b,"input":b}`. The firmware answers only with exactly two parameters and reads them by position; `{"Channels":[]}` when it couldn't take its I/O lock within 1 s ("busy"). `pulseActive` and `timerActive` are true while a pulse or a timer started on the board's own page runs on that relay (`pulseStatus.status[j]`, `timerStatus.status[j]`). The tasks hand each change of them over a queue that holds one entry, and each of the two `sendIOState` takes it into its own copy: the one for `/status` and `/relay`, or the one for `/mosfet`, which answers without them. So a change the `/mosfet` answer took first doesn't show in the status until the next change of a pulse or timer (or a `/relay`, which stops that relay's).
- `POST /relay`, form `index=N&state=0|1&interface=-1&socket=0` → the same status. One relay per request: a mask is sent as one request per relay, all of them even when one answer can't be read (an error, `{"Channels":[]}`, broken JSON), and the last readable answer is the state, with a read at once to confirm the rest. The command returns the relays whose answer could be read. When no relay's answer can be read (always so for a single relay), the command throws "The VM208 didn't take the command: …", as the VM204 does, so nothing counts as switched, and the poller counts it as a failed poll, also when every answer was `{"Channels":[]}` (busy) after the retries below. A request that gets no answer at all stops the rest and counts as a failed poll. Either way the board is read at once. Read by position: a missing parameter or a relay outside 1–4 crashes the board, so the app only ever sends all four, with relays 1–4.
- `GET /getalarms?channel=N` → relay N's week schedule: `{"Channel":{"alarms":[{"dow":d,"hour":h,"minute":m,"onoff":b,"enabled":b} ×14]}}`, the 7 "on" alarms Sunday (`dow` 0) first, then the 7 "off" alarms Sunday first (`Vm208Alarm`). The handler reads its first parameter by position, unchecked, as a 16-bit number and looks up `getShedule(N - 1)`: no parameter, or channel 0 (index 65535, a null the handler then uses), crashes the board, and a channel above 4 is an extension module's. The app only ever sends `channel=1` to `channel=4` (`Vm208Api.AlarmsQuery`).
- `POST /shedule_set`, form `relay=N&alarm_time_1=HH%3AMM&alarm_enable_1=true|false&…&alarm_time_14=…&alarm_enable_14=…` (29 parameters, as the board's own page sends them) → sets relay N's whole schedule, saves it to flash (`writeAlarm`) and answers the board's settings. Read by position: pairs 1–7 are the "on" alarms Monday to Sunday, 8–14 the "off" alarms Monday to Sunday, the time as `substring(0, 2)` and `substring(3, 5)`, enabled only for the word `true`. Any other number of parameters is answered 400. The relay goes unchecked into a 16-bit index, so relay 0 writes outside the table: the app only sends relays 1–4 (`Vm208Api.ScheduleForm`).
- Not used: `POST /setalarm` changes one alarm in memory only, and the board's restart brings the saved one back. `/stoptimer` and `/stoppulse` toggle the relay before they stop anything. `/pulse` and `/timer` (the board's own page starts pulses and timers with them).
- No login: HTTP basic auth protects only the board's own pages, never `/status` or `/relay`, so `Vm208Model` has login length 0 and `Vm208Connection` takes no login but none. `m1`/`m2` are the two transistor outputs, not relays: read and traced, never counted.

### The app's client

- **Busy:** a status or relay request answered `{"Channels":[]}` is sent again, up to 2 more times, 250 ms apart (`Vm208Api.BusyRetries`, `BusyWait`, on `TimeProvider`), traced "Busy: asking again (1 of 2)"; a busy answer that is asked again isn't traced as an error. Never again to another board: not after the address changed, and for a command not once the board counts as lost (`EnsureSameBoard`), which also holds for the rest of a mask. A command holds the poller's lock while it waits: at worst, four relays each answering busy at the end of its 5 s, about 4 × (3 × 5 s + 2 × 0.25 s) = 62 s. Only then does it count, as any answer the app can't read: a poll as a failed poll, a relay as one whose answer couldn't be read. A relay request sets a state (`state=0|1`), never toggles it, so sending it again is harmless, also when the board did switch under its shorter lock. The retries are the VM208's own, not `BoardPoller`'s. The simulator answers busy for `BusyNext` statuses.
- `Vm208Client`: `HttpClient` (no proxy, answers capped at 8 KiB since relay names have no length limit, 5 s per request on `TimeProvider`), polling through `BoardPoller` (`Vm208:PollSeconds`, default 2). `Vm208Json` reads the answer strictly where the app uses it (each `state`, `input`), after UTF-8 decoding with replacement, so a broken name can't make every status unreadable. `Vm208Board` (4 relays, 1 input, doesn't report changes, its own rules `Vm208Api.OwnRules`: `Reads`, `TurnsOff` and `MayHaveUnseen`), `Vm208Connection`, `Vm208Traffic` and `Vm208Model` (id `vm208`, port 80) are its adapters, `Features/Vm208` registers them.
- **The board's schedules** (issue #513): the first poll of each connection, once the status answered, reads every relay's schedule (`GET /getalarms`, relays 1–4, one request each), and then every minute (`Vm208Client.SchedulesInterval`, on `TimeProvider`). The board has no revision counter, so each check costs 4 requests; once a minute adds them to about 30 polls, as the Waveshare's modes and the Shelly's settings are read, and whatever a schedule switches shows at the next poll anyway. `Vm208Json.ReadAlarms` reads them strictly: 14 alarms in the firmware's order, times 00:00–23:59, so a schedule can always be written back exactly. A relay with an enabled alarm has a `Timer` rule, each relay its own unit (`BoardState.OwnRules`). An answer that isn't 200 or can't be read, or no answer, stops the read and makes the rules null; it never fails the poll or loses the board, and the read is tried again a minute later. A change of the rules alone comes as `OwnRulesChanged`; a command's answer carries the rules as last read.
- **Pulses and timers** (issue #552): every status's `pulseActive` and `timerActive` (`Vm208Status.Pulses`, `Timers`, `Running`). A relay with one running has a `Timer` rule too, which the app can't turn off (`OwnRules.CantTurnOff`), also when its schedule is on: turning the schedule off wouldn't remove the rule. When one relay's flag isn't true or false, the flags are unknown and the rules null; the status is still read, so it never fails the poll. A change of a run alone changes the rules, so it comes as `OwnRulesChanged`, or with `StateChanged` when a relay switched too (a run toggles its relay at its start and its end). The Debug screen shows it in the status: "Status: relay 3 on · input off · timer running on relay 3".
- **Turning them off** (`TurnOffOwnRulesAsync`, only on a person's press): `Timer` rules for relays 1–4 only (anything else is `ArgumentOutOfRangeException`, before anything is sent), a rule given twice once, at once or `BoardNotConnectedException`, never queued. For each relay, in relay order: its schedule read fresh, one `POST /shedule_set` with its 14 alarms at their own times and all disabled, and the schedule read back; then the status. A relay whose read or save isn't answered 200 is left and the others still go; no answer stops the rest. The command reads the status first: a relay with a pulse or a timer running then is left, and so is every relay while the flags can't be read, since one may run. Nothing is sent for such a relay, and it isn't taken; when that leaves none, the command throws `BoardNotConnectedException` with nothing sent but the status. A run started on the board's page between that read and the save still gets its schedule turned off, and is then a rule the app can't turn off. It returns the rules whose save was answered 200, and throws `BoardNotConnectedException` when there are none. The answer to `/shedule_set` is the board's settings (its user name, network name, e-mail settings): the Debug screen shows only its status code.

### The simulator

- `Vm208Simulator`: a small HTTP server on `TcpListener` with the same routes, relays and the input set at the board, refused or dropped connections, and no answer where the firmware gives none or would crash. The simulator's page offers port 8208.
- It holds each relay's schedule, the factory one to begin with (`FactorySchedule`, as `data/alarms/0.json`: every alarm off), answers `GET /getalarms` and `POST /shedule_set` as the firmware does (400 for other than 29 parameters), and gives no answer for no channel, channel 0 or a channel above 268 (the firmware crashes) or 5–268 (an extension module's, not simulated). A time out of range, such as 99:00 or -1:30, is kept as the firmware keeps it, in its `uint8_t` fields (99:00, 255:30), and answered so by `GET /getalarms` (`ChannelShedule::setShedule`; the board never runs such an alarm); the app reads such a schedule as unreadable, so the rules are unknown, never none. A request the simulator fails on goes unanswered, never faults its connection. It keeps the schedules but doesn't run them. Tests set them with `SetAlarmsAsync`; as an `ISimulatedBoardTimers`, the simulator's page turns every alarm of a relay on or off at its time.
- Pulses and timers, as on the board's own page: `StartPulseAsync(relay, milliseconds)` and `StartTimerAsync(relay, minutes)` (1–60000, as the page takes them), and on the simulator's page "Pulse 5 s" and "Timer 1 min" per relay. As `pulseTask` and `timerTask` run them: the relay toggled at once and again at the end, on the simulator's `TimeProvider`; the status shows them meanwhile; a second one on the same relay toggles it again and starts the time anew; `POST /relay` for the relay stops its pulse and its timer without the toggle at their end, after its answer, which still shows them (the firmware only queues the stops, and its tasks take them on their own loop); a power cut forgets them. A timer's minutes are multiplied into the 16-bit `time` (`auto time = param.time; time *= 60000;`), so it runs (minutes × 60000) mod 65536 ms: 1 minute 60 s, 2 minutes 54.464 s, never more than 65.535 s (`Vm208Simulator.TimerLength`). The routes `/pulse` and `/timer` aren't simulated, and neither is the `/mosfet` answer taking a change of the flags.
- A power cut (`ISimulatedBoardPower`): the firmware doesn't say how the relays come back (see "To check on a real board"), so the simulator brings them all back off. The saved schedules stay, as `writeAlarm` saves them; pulses and timers are gone.

### Settings on the board that switch relays without the app

- **The week schedules:** each relay has 14 alarms, an "on" and an "off" time for each day, set on the board's own page and saved to its flash. They switch relays without the app (`ChannelShedule::Update`, checked every 500 ms), so they compete with the app's schedules. The app reads them and reports a `Timer` rule for each relay with an enabled alarm.
- **What the app does:** it never changes them on its own accord. It shows the ones it finds, warns, and turns a relay's schedule off only when a person presses "Turn them off", with one `POST /shedule_set` that keeps each alarm's time and disables it (see `docs/ARCHITECTURE.md`, "The board's own rules").
- **Pulses and timers** (`/pulse`, `/timer`) are not saved: a request starts one, it toggles the relay at its start and at its end, and it lives in memory only (`src/PulseAndTimer.cpp`), gone at a restart. The app never starts one and never stops one (`/stoppulse` and `/stoptimer` toggle the relay first). One started on the board's own page switches the relay again without the app when it ends. The source says that is within 65.535 s, also for a timer of many minutes (see the simulator above); that is still to be checked on a real board. The app reads it in the status and reports a `Timer` rule for that relay while it runs, which it can't turn off: the screens warn about it, offer no "Turn off" for it, and say "Started on the board's own web page. It ends by itself, and Contactor can't stop it." (see `docs/ARCHITECTURE.md`, "The board's own rules"). Switching the relay from the app stops its pulse and timer, as the firmware's `/relay` does. The status can miss one (see `/status` above), so the VM208 still states `MayHaveUnseen` with `Reads` and `TurnsOff` (`Vm208Api.OwnRules`), as the Shelly devices do: the board's page says "This board may also have rules Contactor can't see. Check its own web page.", under the rules it found and alone when it found none.
- **What the app sees:** every poll shows the relays, also those a schedule, a pulse, a timer or a button switched. The app logs a change it didn't ask for with Actor `external`, and a group's interlock checks every status.
- **What to set:** make the schedules in the app, not on the board, and turn the board's schedules off when the app asks.

## To check on a real board

> **Warning: check the first three points of the VM201 list and the first two points of the VM204 list and the first three points of the VM208 list first,
> before anything is connected to the relays.**

### VM201

- Whether the physical relay state survives a power cut, and if so in which state.
- CMD_PULSE: that the time is a binary number (not ASCII digits), and the units lower case. Velleman's description gives
  only "pulse time: 1...99" and the units; the reference implementation doesn't send it.
- CMD_PULSE: whether the board pushes `CMD_STATUS` both when the pulse starts and when it ends, what an ON or OFF of the
  relay during a pulse does to it (the simulator ends the pulse), what a second pulse does (the simulator starts it
  again), and what a time of 0 or above 99 does (the simulator ignores the packet).
- How many TCP clients the board allows at once. Assume **one**: the app holds exactly one connection.
- Whether the board closes idle connections. That is why `CMD_STATUS_REQ` is sent every 15 seconds as a keep-alive.
- CMD_TMR_DIS with a mask, e.g. `0x14`: that it turns off only those relays' timer programs and leaves the others, that
  the next `CMD_STATUS` shows the new timers (byte 2), whether turning a timer off also switches its relay (the
  simulator leaves the relays as they are), and whether a timer turned off stays off after a power cut.

### VM204

- That `relay/on` and `relay/off` answer properly (their handler in `httpServerUriNotFoundCallback` has a missing return).
- Whether an input reads `true` closed or open.
- Whether the relays survive a power cut, and if so in which state (the simulator brings them back off).
- That the status right after a command already shows it.
- Logins with symbols and the 48-character total (the firmware's `authUser` also reads past the end of the query).
- The key's length and characters (its buffer is 32 bytes and the terminator `gen_random` writes overruns it).
- Days of polling every 2 s.
- The status JSON as above.
- The analog input: that 0 V reads 0 and 6.6 V reads 1023 (or about 3.3 V reads 512), with the board's own page showing the same voltage as the app.

### VM208

- That the answer to `/relay` already shows the change (if the firmware can't take its lock within 100 ms the relay isn't switched).
- Whether the input reads `true` closed or open (it is the raw pin level).
- Whether the relays survive a power cut, and if so in which state (the simulator brings them back off).
- That switching a relay stops its pulse or timer, without toggling it, and whether the `/relay` answer already shows the run stopped (the source suggests it doesn't: the stop is only queued; the simulator shows it at the next status).
- A pulse and a timer started on the board's own page: that `/status` shows `pulseActive` or `timerActive` true on that relay while it runs and false after, and that the app warns about it with no "Turn off".
- How long a timer of 2 and of 10 minutes really runs: the source says 54.464 s and 10.176 s (the 16-bit `time` in `timerTask`).
- Whether pressing a transistor output (m1, m2) on the board's own page right after starting a pulse or a timer hides it from `/status`, as the source suggests.
- That the board's own schedules and buttons show on the next poll.
- `GET /getalarms?channel=1` to `4`: that the answer has the shape above, with the times the board's own page shows.
- `POST /shedule_set` from the app: that the board answers 200, that the page then shows every alarm of that relay disabled at its old time, that the other relays' schedules are untouched, and that they stay so after a power cut (`writeAlarm` only writes a file that already exists).
- That turning a schedule off doesn't switch the relay.
- The status JSON as above.
- How it copes with polling every 2 s for days.
- `/status` with an extension module plugged in.
