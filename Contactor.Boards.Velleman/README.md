# Velleman

How the app talks to the VM201, the VM204 and the VM208. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is in `docs/ARCHITECTURE.md`.

The app talks to the VM201 over its own TCP protocol, and to the VM204 and the VM208 over HTTP. The three boards have different protocols, so each has its own types, named after the model (`Vm201*`, `Vm204*`, `Vm208*`), and its own section below.

## Sources

- VM201: Velleman's protocol description, via https://github.com/tlrh314/misc/tree/main/vm201 (a Python reference implementation). Confirmed with a packet dump from the Whadda forum.
- VM204: Velleman's firmware, https://github.com/Velleman/VM204-Firmware, branch V1.1 (CycloneTCP); each route names its firmware file in `Vm204Api`. For the analog input also Velleman's *User Manual – Ethernet Relay Card VM204*, December 2016, https://cdn.velleman.eu/downloads/0/user/usermanual_vm204_en.pdf.
- VM208: Velleman's firmware, https://github.com/Velleman/VM208 (`src/Network/server_VM208.cpp`, ESPAsyncWebServer, ArduinoJson 5), and its library https://github.com/velleman/VM208INT; each route names its firmware file in `Vm208Api`.

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
  - Holds one persistent connection. On failure: reconnect with exponential backoff (1 s → max 30 s).
  - Exposes `Vm201State` (Outputs byte, TimerMask, Input, `IsConnected`, `LastContactUtc`) and an event or `IObservable` when the status changes.
  - Commands (`TurnOnAsync(mask)`, `TurnOffAsync(mask)`, `PulseAsync(mask, length)`, `RequestStatusAsync()`, `TurnOffTimersAsync(mask)`) are serialized through a `Channel<T>`, so only one writer is used at a time, in the order they were sent. A command cancelled before the writer takes it is not sent, and its caller gets `OperationCanceledException`. A cancel after that comes too late: the command goes out, and its caller gets the write's own result.
  - `PulseAsync` sends the whole mask in one CMD_PULSE. A length goes out exactly or not at all (`Vm201Pulse`): whole seconds up to 99 s, else whole minutes up to 99 min, else whole hours up to 99 h. Any other length, such as 100 s or 90.5 s, throws `ArgumentOutOfRangeException` before anything is sent.
  - Timeouts: connect 5 s, login 5 s.
  - When the board refuses the login, `Vm201State.Problem` says why (a `ConnectionProblem`): `LoginNeeded` when the board asked for a login (CMD_AUTH) and the app has none, else `LoginRefused`. It never holds the login. The client clears it at the next login, in `SetLogin` and `SetEndpoint`, and when a later connection gets no answer (the board is gone, so it no longer says why); a refusal from a connection that the app ended meanwhile is dropped.
  - `SetEndpoint` and `SetLogin` change the address and login while it runs: the current connection, attempt or wait ends at once. The web app applies the address and login saved on its `Board` row before the first connection. `Vm201Login` is a class, not a record, so its password can't leak through a generated `ToString`.
  - The `Traced` event reports every packet in and out and every connection step (connecting, lost and why, the wait before the next attempt, skipped bytes) as a `Vm201TraceEntry`. User name and password packets come cut to their first three bytes. The web app keeps the latest 100 in memory (`BoardTrace`) for the Debug screen; the keep-alive and the channel names are routine, so it only counts them.
- `Vm201Board`: the VM201 as an `IRelayBoard` (8 relays, 1 input, reports changes, a pulse of 1 s to 99 h in steps of 1 s); it translates `Vm201State` and refuses relays above 8. Its own rules (`BoardState.OwnRules`) are its timer programs: a `Timer` rule per bit of the status's byte 2, in relay order; null before its first status (also at a new address), and kept while disconnected. The client raises its state for every status, the keep-alive's too, so a change of the timers alone comes with `StateChanged`, and the board never raises `OwnRulesChanged`. `TurnOffOwnRulesAsync` takes only `Timer` rules for relays 1–8 (anything else is `ArgumentOutOfRangeException`, before anything is sent), takes a rule given twice once, and sends one `CMD_TMR_DIS` with only their relays' bits: relays 3 and 5 are `02 06 44 14 A0 03`, never `0xFF`. It goes like any other command, at once or `BoardNotConnectedException`, never queued, and returns the rules sent; for none it sends nothing. Its `PulseAsync` refuses a length the board can't send exactly with `ArgumentOutOfRangeException`. Its pulse ability (`Vm201Pulse.Ability`) checks each length with `TryEncode` (`PulseAbility.Exact`), so `CanDo` also refuses such a length: `CanDo(100 s)` is false.
- `Vm201Connection` and `Vm201Traffic`: the client as an `IBoardConnection` and an `IBoardTraffic` (`Vm201TraceText` puts each packet in plain words). `Vm201Model` is its catalogue entry.

### Settings

`Vm201:LogPackets` (off by default) logs every packet in and out as hex with its command letter (`Vm201PacketTrace`); user name and password packets only as their command and length. The board keeps no log of its own, so this is how to see what a real board does.

### The simulator

- `Vm201Simulator`: a `TcpListener`-based fake that implements the whole protocol, optional auth, push on change, a pulse that ends by itself on its `TimeProvider` and a way to simulate a disconnect, as an `ISimulatedBoard` and `ISimulatedBoardTimers`. Its traffic uses the same words as the app's Debug screen (`Vm201TraceDescriber`). It is used in the tests and in development, where `Contactor.Simulator` runs it as its own process through Aspire. The web app has no built-in simulator: it always connects to `Vm201:Host`.

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

- `Vm204Client`: `HttpClient` (no proxy, answers capped at 4 KiB, 5 s per request on `TimeProvider`), one request at a time; logs in for the key and polls through `BoardPoller` (`Vm204:PollSeconds`, default 2). A refused key is dropped and the next poll, asked for at once, logs in again. When the board answers 400 to `/auth` at the connect, `Vm204State.Problem` says why (a `ConnectionProblem`): `LoginNeeded` when the app has no login, else `LoginRefused`; a refused key is no problem. It never holds the login, and `BoardPoller` clears it at the first answer, at a new address and at a new login. Commands throw `BoardNotConnectedException` while disconnected, never wait behind a poll, and their answer (the status) is the confirmation. A change of the analog value alone isn't a state change: `BoardPoller`'s comparer (`Vm204Status.SwitchesComparer`) leaves it out, and it comes as the client's `AnalogChanged` instead of `StateChanged`. Its `Traced` event gives each request and answer in plain words (`Vm204TraceText`): the key, the user name and the password never show, and the polls and unchanged answers are routine.
- `Vm204Json` reads the answers strictly (anything else is a `Vm204ProtocolException` and counts as a failed poll). `Vm204Board` (4 relays, 4 inputs, 1 analog input, doesn't report changes; the analog value in `BoardState.Analog`, and a change of it alone as `AnalogChanged`), `Vm204Connection`, `Vm204Traffic` and `Vm204Model` (id `vm204`, port 80) are its adapters, `Features/Vm204` registers them.

### The simulator

- `Vm204Simulator`: a small HTTP server on `TcpListener` with the same routes and 400s as the firmware, an optional login, relays and inputs set at the board, the analog value (0–1023, `SetAnalogAsync`, a slider on the simulator's page), a new key, and refused or dropped connections; a connection that sends no whole request head within 5 s is closed. The simulator's page offers port 8204 for a new one (`SimulatorModel.SuggestedPort`), as 80 needs rights a developer doesn't have.

## VM208

The VM208 (ESP32, WLAN or Ethernet) is neither the VM201's nor the VM204's protocol.

### Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `vm208` | Velleman VM208 | 4 | 1 | none | none |

Only the card itself is used (`interface=-1`); VM208EX extension modules are left out for now.

### Protocol

Plain HTTP on port 80, JSON answers, one request per connection. It never reports a change by itself, so the app polls.

- `GET /status?interface=-1&socket=0` → `{"Channels":[{"name":"…","state":b,"pulseActive":b,"timerActive":b} ×4],"m1":b,"m2":b,"input":b}`. The firmware answers only with exactly two parameters and reads them by position; `{"Channels":[]}` when it couldn't take its I/O lock within 1 s ("busy").
- `POST /relay`, form `index=N&state=0|1&interface=-1&socket=0` → the same status. One relay per request: a mask is sent as one request per relay, all of them even when one answer can't be read (an error, `{"Channels":[]}`, broken JSON), and the last readable answer is the state, with a read at once to confirm the rest. The command returns the relays whose answer could be read. When no relay's answer can be read (always so for a single relay), the command throws "The VM208 didn't take the command: …", as the VM204 does, so nothing counts as switched, and the poller counts it as a failed poll, also when every answer was `{"Channels":[]}` (busy) after the retries below. A request that gets no answer at all stops the rest and counts as a failed poll. Either way the board is read at once. Read by position: a missing parameter or a relay outside 1–4 crashes the board, so the app only ever sends all four, with relays 1–4.
- No login: HTTP basic auth protects only the board's own pages, never `/status` or `/relay`, so `Vm208Model` has login length 0 and `Vm208Connection` takes no login but none. `m1`/`m2` are the two transistor outputs, not relays: read and traced, never counted.

### The app's client

- **Busy:** a status or relay request answered `{"Channels":[]}` is sent again, up to 2 more times, 250 ms apart (`Vm208Api.BusyRetries`, `BusyWait`, on `TimeProvider`), traced "Busy: asking again (1 of 2)"; a busy answer that is asked again isn't traced as an error. Never again to another board: not after the address changed, and for a command not once the board counts as lost (`EnsureSameBoard`), which also holds for the rest of a mask. A command holds the poller's lock while it waits: at worst, four relays each answering busy at the end of its 5 s, about 4 × (3 × 5 s + 2 × 0.25 s) = 62 s. Only then does it count, as any answer the app can't read: a poll as a failed poll, a relay as one whose answer couldn't be read. A relay request sets a state (`state=0|1`), never toggles it, so sending it again is harmless, also when the board did switch under its shorter lock. The retries are the VM208's own, not `BoardPoller`'s. The simulator answers busy for `BusyNext` statuses.
- `Vm208Client`: `HttpClient` (no proxy, answers capped at 8 KiB since relay names have no length limit, 5 s per request on `TimeProvider`), polling through `BoardPoller` (`Vm208:PollSeconds`, default 2). `Vm208Json` reads the answer strictly where the app uses it (each `state`, `input`), after UTF-8 decoding with replacement, so a broken name can't make every status unreadable. `Vm208Board` (4 relays, 1 input, doesn't report changes), `Vm208Connection`, `Vm208Traffic` and `Vm208Model` (id `vm208`, port 80) are its adapters, `Features/Vm208` registers them.

### The simulator

- `Vm208Simulator`: a small HTTP server on `TcpListener` with the same routes, relays and the input set at the board, refused or dropped connections, and no answer where the firmware gives none or would crash. The simulator's page offers port 8208.

### Settings on the board that switch relays without the app

- The board's own schedules, pulses and timers compete with the app's and are left as they are (the firmware has `POST /setalarm` and `/shedule_set` that could turn the schedules off).

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
- That the status right after a command already shows it.
- Logins with symbols and the 48-character total (the firmware's `authUser` also reads past the end of the query).
- The key's length and characters (its buffer is 32 bytes and the terminator `gen_random` writes overruns it).
- Days of polling every 2 s.
- The status JSON as above.
- The analog input: that 0 V reads 0 and 6.6 V reads 1023 (or about 3.3 V reads 512), with the board's own page showing the same voltage as the app.

### VM208

- That the answer to `/relay` already shows the change (if the firmware can't take its lock within 100 ms the relay isn't switched).
- Whether the input reads `true` closed or open (it is the raw pin level).
- That switching a relay stops its pulse or timer.
- That the board's own schedules and buttons show on the next poll.
- The status JSON as above.
- How it copes with polling every 2 s for days.
- `/status` with an extension module plugged in.
