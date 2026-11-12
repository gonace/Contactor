# Contactor – the relay app

Contactor is an open-source mobile web app for controlling the 8 relays on a **Velleman VM201** ethernet relay board from a phone, e.g. in a workshop, where the relays drive fan heaters, a compressor, ventilation, lighting and more. It started as the app for one workshop, run by a licensed electrician and a few friends.

The users are not technical. **Simplicity comes before features**: big buttons, plain words, no menus that aren't needed.

The screens are described under User interface below; the running app is the reference for layout, texts, colours and sizes.

---

## Languages

- **Everything is in English:** the UI, code, comments, log messages, tests, commit messages, pull requests, issues, `README.md` and this file. Translations of the UI may come later.

---

## Architecture

```
Phone ──HTTPS──▶ <your hostname>: Cloudflare Access (e-mail OTP) ──▶ Cloudflare Tunnel
                                                         │
                                cloudflared, started by the app in its container on the Raspberry Pi
                                                         │ http://contactor:8080 (compose network)
                                              Contactor.Web (Blazor Server, .NET 10)
                                                 │                    │
                                           SQLite (WAL)        TCP :9760 ──▶ VM201
```

- **No port is opened to the internet.** All access goes through Cloudflare Tunnel + Access on the installation's own hostname. Each installation has its own Access team domain (`<team>.cloudflareaccess.com`) and application; the AUD tag is in Zero Trust → Access controls → Applications → Edit → Additional settings.
- **Sign-in is handled entirely by Cloudflare Access** (one-time PIN by e-mail, session length 1 month, an allow list of e-mail addresses). The app has no sign-in of its own.
- The app runs in a container and listens on port 8080 on all of the container's addresses. It starts `cloudflared` itself when `Tunnel:Token` is set, and starts it again with a growing wait (1 s doubling to 1 min) whenever it stops (`Features/Tunnel`); the image has no shell or process manager. The token goes to `cloudflared` only through its environment and is never logged. The port is **never published** on the Raspberry Pi, so nobody on the LAN can get around Access.
- Give the Pi and the VM201 DHCP reservations in the router, so their addresses don't change.

## Stack and structure

- .NET 10 (LTS), C#, Blazor Server (interactive server rendering), EF Core + `Microsoft.EntityFrameworkCore.Sqlite`
- No JavaScript framework. No external CSS framework; one small stylesheet of our own, following the design tokens below.
- Target: one container image (`ghcr.io/gonace/contactor`, linux/arm64 and linux/amd64), built from the `Dockerfile` on Ubuntu chiseled-extra, which includes the time zone data the schedules need, with `cloudflared` copied in; everything it keeps is in `/data`. Run with Docker Compose on the Raspberry Pi.
- Local development with Aspire 13.5.4, pinned on purpose: later releases hang at startup on Windows. Its advisory GHSA-f7vh-h5g3-7jrh (the dashboard's cookie reaching a container opened from it) is suppressed in the AppHost only, which runs no containers; raise the version once a release starts on Windows (issue #18). The AppHost starts the simulator (a VM201 on port 9760, and its page linked from the dashboard) and the web app against it.

No `src/` or `tests/` folders: every project sits directly in the repository root. All tests live in **one** test project, `Contactor.Tests`, with a folder per area (`Vm201/`, `Web/` …). Don't create more test projects.

```
Contactor.slnx
Dockerfile                  # The image: the app and cloudflared, for linux/arm64 and linux/amd64
global.json                 # SDK version and test runner (Microsoft.Testing.Platform)
Directory.Build.props       # Shared build settings
Directory.Packages.props    # Central package management: every package version is here
Contactor.Boards/           # What any relay board is to the app: IRelayBoard, IBoardConnection, IBoardTraffic, BoardModel, BoardPoller. No dependencies.
Contactor.Boards.Vm201/     # The VM201: packet codec, client, simulator and its adapters (Vm201Board, Vm201Connection, Vm201Traffic). No web/EF.
Contactor.Boards.Vm204/     # The VM204: HTTP client, simulator and its adapters (Vm204Board, Vm204Connection, Vm204Traffic). No web/EF.
Contactor.Boards.Vm208/     # The VM208: HTTP client, simulator and its adapters (Vm208Board, Vm208Connection, Vm208Traffic). No web/EF.
Contactor.Web/            # Blazor app, EF Core, background services, identity
  Persistence/              # DbContext, entities (one file per entity) and migrations; not a feature
  Features/<Feature>/       # One folder per feature; Features/Boards is the board catalogue, Features/<Board> what only that board has
Contactor.Simulator/      # Development: simulated boards, each on its own port, with a page to drive them and see their traffic
Contactor.AppHost/        # Aspire: runs everything locally (dotnet run --project Contactor.AppHost)
Contactor.ServiceDefaults/ # Aspire defaults: OpenTelemetry, health checks, resilient HTTP clients
Contactor.Tests/          # The only test project
  Boards/                   # ChannelMask, BoardPoller, board adapters
  Simulator/                # The simulator's boards and page
  Vm201/                    # Codec + client against the simulator
  Vm204/                    # JSON, login, trace words + client against the simulator
  Vm208/                    # JSON, trace words, connection + client against the simulator
  Web/                      # Scheduling, auto-off, daylight saving, identity
deploy/
  compose.yml               # The app (cloudflared inside) on the Raspberry Pi; no published ports
  install.sh                # Run on the Pi: settings to /opt/contactor/.env, data folder, ghcr.io login, first start
  upgrade.sh                # Pulls, checks the cosign signature, restarts, waits until healthy
  autoupdate-install.sh     # systemd timer: upgrade every night 03:00–04:00
  cosign/                   # Public signing keys and how to make them
docs/screenshots/           # The README's screenshots, taken by scripts/screenshots/take.mjs
scripts/
  screenshots/take.mjs      # Takes them: simulator, app and made-up data on a temporary database, Playwright
  release-version.sh        # YYYY.M.D[.N]
  images-build.sh           # Build and push the image with docker buildx; prints the digest to sign
  images-publish.sh         # Publish and sign the image from your computer
  images-sign.sh            # cosign, by digest
DEPLOY.md                   # Installation steps
LICENSE                     # MIT
THIRD-PARTY-NOTICES.md      # Others' work we include or bundle; their licence texts in licenses/ (in the image: /licenses)
.github/workflows/build.yml # build + test (pull requests, merges to main, by hand)
.github/workflows/images.yml # publish and sign the image (by hand)
.github/pull_request_template.md # What every pull request description has
.agents/writing.md          # How to write pull requests, issues and comments
.agents/review.md           # The independent review's checklist
.claude/agents/code-reviewer.md # The reviewer as a Claude Code agent
```

---

## VM201 TCP protocol (port 9760)

Source: Velleman's protocol description, via https://github.com/tlrh314/misc/tree/main/vm201 (a Python reference implementation). Confirmed with a packet dump from the Whadda forum.

### Packet format

```
<STX><LEN><CMD><data_1>...<data_n><CHECKSUM><ETX>
STX = 0x02
ETX = 0x03
LEN = total number of bytes in the packet, including STX and ETX
CHECKSUM = two's complement of the sum of all preceding bytes: (byte)(-(sum(STX..last data) % 256) & 0xFF)
```

Note: the comment header of the original document wrongly says STX=0x0F/ETX=0x04. **0x02/0x03 is right.**

Verified example: turn on relay 1 = `02 06 4F 01 A8 03` (0x02+0x06+0x4F+0x01 = 0x58 → 0x100−0x58 = 0xA8).

### Commands

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
| CMD_TMR_DIS | `'D'` | 6 | client → board | Disable the board's timer programs (mask) |
| CMD_TMR_TOGGLE | `'G'` | 6 | client → board | Toggle the board's timer programs (mask) |

**Channel mask:** bit 0 = channel 1 … bit 7 = channel 8.

**CMD_STATUS data:** byte 1 = outputs (one bit per channel, 1 = ON), byte 2 = the board's timers (1 = active), byte 3 = input (bit 0, 1 = ON).

### Session flow

1. Connect over TCP to `host:9760`.
2. The board sends `CMD_LOGGED_IN` (no auth) **or** `CMD_AUTH`. On `CMD_AUTH`: send `CMD_USERNAME` and then `CMD_PASSWORD`. The answer is `CMD_LOGGED_IN`, or `CMD_ACCESS_DENIED` + `CMD_CLOSED`.
3. After login the board sends **9 `CMD_NAME`** (8 outputs + the input) and then **`CMD_STATUS`**.
4. From then on the board sends **`CMD_STATUS` on its own at every change**, including changes made through the board's web interface, its buttons or its own timers.
5. End with `CMD_CLOSED`.

### Known quirks

- Channel names can contain garbage characters from the firmware. **The app ignores the board's names**; the names are stored in SQLite.
- An ON/OFF that changes nothing gets **no** `CMD_STATUS` reply. Never wait for a reply after a command. Rely on the pushed status and on a periodic `CMD_STATUS_REQ`.
- Read the stream with a real frame parser: read STX, LEN, then the remaining LEN−2 bytes. Never assume one `Read` returns exactly one packet: TCP can split and merge packets.
- Validate the checksum and ETX. Log and drop invalid packets, but don't disconnect.

### Unknown – verify against the real hardware

- How many TCP clients the board allows at once. Assume **one**: the app holds exactly one connection.
- Whether the board closes idle connections. That is why `CMD_STATUS_REQ` is sent every 15 seconds as a keep-alive.
- Whether the physical relay state survives a power cut, and if so in which state.

---

## Boards (`Contactor.Boards`)

The app talks to a relay board only through `IRelayBoard`, `IBoardConnection` and `IBoardTraffic`, and knows its model from the board catalogue, so another make of board needs its own project with those adapters, a reference from `Contactor.Web` and a line in the catalogue, and nothing else changes. One board per installation for now; several at once is issue #4.

- `IRelayBoard`: `Capabilities`, `State`, `StateChanged`, `TurnOnAsync`/`TurnOffAsync(ChannelMask)` and `RefreshAsync`. Commands go at once or throw `BoardNotConnectedException`; they are never queued. A command isn't confirmed by its return: the next `StateChanged` is the confirmation.
- `BoardState`: `IsConnected`, the relays and digital inputs that are on, and `LastContactUtc`. The board is the source of truth; the app never stores relay state.
- `BoardCapabilities`: how many relays and inputs (and analog inputs, which the app doesn't show yet), and whether the board reports changes by itself.
- `BoardPoller<TReading>`: the loop for a board that doesn't report changes by itself (the VM204 and the VM208). The board's own code implements `IPolledBoard<TReading>` (connect, e.g. log in; read the state once); the poller asks every `PollInterval` (2 s) with `TimeProvider`, refreshes `State` (and so `LastContactUtc`) on every poll but reports a state only when it changed (a comparer decides what counts), counts the board as lost after `FailuresBeforeLost` (3) failed polls in a row (a command that got no answer, `IsNoAnswer`, counts as one, so a dead board is lost even while commands keep coming), and connects again with backoff 1 s → 30 s. Commands go through `RunCommandAsync`: a poll under way is cancelled (that doesn't count as a failure) so the command goes at once, it throws `BoardNotConnectedException` once the board is lost (decided under the same lock as the polls), and what the command returns, the board's state right after it, is taken as a poll's answer. A command that fails (refused, unreadable or no answer) is followed by a read at once, so the screens show what the board really did; a command without an answer counts once, and the read after it is an ordinary poll that counts on its own, so a dead board is lost after 3 unanswered requests in a row. `PollNow` asks at once, `Restart` ends the connection or wait (new address or login).
- `ChannelMask`: a set of channels (relays or inputs) numbered from 1, at most 64; bit 0 is channel 1.
- `IBoardConnection`: where the board is (`BoardEndpoint`) and its login (`BoardLogin`), with the defaults from the board's own settings, changeable while the app runs; `CheckLogin` says what the board can't take, and `RunAsync` keeps the connection open.
- `IBoardTraffic`: each step on the connection as a `BoardTraceEntry` in plain words (direction, text, the bytes, error, routine), for the Debug screen and the toasts. The board writes the words and never puts a password in one.
- **The board catalogue** (`Features/Boards/BoardCatalogue`): every model the app supports, as a `BoardModel` (id, name, capabilities, default port, login length and an optional login hint in plain words) and how to register it. The model chosen in the setup guide (`Installation.BoardType`, read before the services are built: `ReadSavedBoardType`) wins; until one is saved, `Board:Type` chooses (default `vm201`). The app refuses to start on a model the catalogue doesn't have. A model is never switched on its own afterwards: replacing the board is issue #32. `AddBoards` registers the chosen model and what every board has in the app: its address and login on the Settings screen (`BoardAddressHandler`), the Debug trace (`BoardTrace`), the Debugging setting and `BoardHostedService`, which keeps the connection open.
- **Adding a model:** a project of its own (`Contactor.Boards.<Model>`) with its `IRelayBoard`, `IBoardConnection` and `IBoardTraffic`, an `Add<Model>` in `Features/<Model>` for what only it has, and one line in the catalogue. Nothing else in the app changes.
- `ISimulatedBoard`: a fake board of a model for development and tests, on a local port: its relays, inputs and connected clients, a relay switched or an input set "at the board", dropped connections, a change event, and the traffic as `BoardTraceEntry` from the board's side (`Sent` is still from the app to the board). A model with timer programs of its own also implements `ISimulatedBoardTimers`. Each model's project brings its own (`Vm201Simulator`, `Vm204Simulator`, `Vm208Simulator`).
- What only one board has stays in its own project and its `Features/<Board>` folder: today the VM201's own timers (`Vm201TimersService`).
- The database holds one relay per relay on the board (`IRelayBoard.Capabilities.RelayCount`, at most 64), seeded at start. Rows above the count, left from a board that had more, are kept but not shown, offered or switched.

## The VM201 client (`Contactor.Boards.Vm201`)

- `Vm201PacketCodec`: `byte[] Encode(byte cmd, ReadOnlySpan<byte> data)` and an incremental decoder over a `PipeReader` or `Stream`.
- `Vm201Client`:
  - Holds one persistent connection. On failure: reconnect with exponential backoff (1 s → max 30 s).
  - Exposes `Vm201State` (Outputs byte, TimerMask, Input, `IsConnected`, `LastContactUtc`) and an event or `IObservable` when the status changes.
  - Commands (`TurnOnAsync(mask)`, `TurnOffAsync(mask)`, `RequestStatusAsync()`, `DisableBoardTimersAsync(mask)`) are serialized through a `Channel<T>`, so only one writer is used at a time.
  - Timeouts: connect 5 s, login 5 s.
  - `SetEndpoint` and `SetLogin` change the address and login while it runs: the current connection, attempt or wait ends at once. The web app applies the saved `BoardSetting` before the first connection. `Vm201Login` is a class, not a record, so its password can't leak through a generated `ToString`.
  - `Vm201:LogPackets` (off by default) logs every packet in and out as hex with its command letter (`Vm201PacketTrace`); user name and password packets only as their command and length. The board keeps no log of its own, so this is how to see what a real board does.
  - The `Traced` event reports every packet in and out and every connection step (connecting, lost and why, the wait before the next attempt, skipped bytes) as a `Vm201TraceEntry`. User name and password packets come cut to their first three bytes. The web app keeps the latest 100 in memory (`BoardTrace`) for the Debug screen; the keep-alive and the channel names are routine, so it only counts them.
- `Vm201Board`: the VM201 as an `IRelayBoard` (8 relays, 1 input, reports changes); it translates `Vm201State` and refuses relays above 8.
- `Vm201Connection` and `Vm201Traffic`: the client as an `IBoardConnection` and an `IBoardTraffic` (`Vm201TraceText` puts each packet in plain words). `Vm201Model` is its catalogue entry.
- `Vm201Simulator`: a `TcpListener`-based fake that implements the whole protocol, optional auth, push on change and a way to simulate a disconnect, as an `ISimulatedBoard` and `ISimulatedBoardTimers`. Its traffic uses the same words as the app's Debug screen (`Vm201TraceDescriber`). It is used in the tests and in development, where `Contactor.Simulator` runs it as its own process through Aspire. The web app has no built-in simulator: it always connects to `Vm201:Host`.

## The VM204 (`Contactor.Boards.Vm204`)

The VM204 is not the VM201's protocol. Source: Velleman's firmware, https://github.com/Velleman/VM204-Firmware, branch V1.1 (CycloneTCP); each route names its firmware file in `Vm204Api`. It is controlled only through its web server: plain HTTP, port 80 unless changed on the board, JSON answers, one request per connection (`Connection: close`), at most 3 connections. It never reports a change by itself, so the app polls.

- `GET /api/auth?user=U&password=P` → `{"ApiKey":"…"}` (32 letters and digits, kept by the board until someone makes a new one on its web page); a wrong login → 400. Factory login `admin` / `VM204`.
- `GET /api/status?key=K` → `{"relays": [b,b,b,b],"pulse": [b,b,b,b],"inputs": [b,b,b,b],"analog": n}`; a wrong key → 400.
- `GET /api/relay/on?key=K&relay=1,3` and `/api/relay/off?…` → switch the listed relays (1–4) and answer the status; a wrong key → 400.
- `/api/*` needs no HTTP basic auth; the board's own pages do. The firmware doesn't percent-decode the query and copies it into small buffers, so a user name or password is at most 31 characters, 48 together, of letters, digits and `- . _ ~ ! $ ' ( ) * + , ; : @` (`Vm204Login`).

- `Vm204Client`: `HttpClient` (no proxy, answers capped at 4 KiB, 5 s per request on `TimeProvider`), one request at a time; logs in for the key and polls through `BoardPoller` (`Vm204:PollSeconds`, default 2). A refused key is dropped and the next poll, asked for at once, logs in again. Commands throw `BoardNotConnectedException` while disconnected, never wait behind a poll, and their answer (the status) is the confirmation; a change of the analog value alone isn't reported. Its `Traced` event gives each request and answer in plain words (`Vm204TraceText`): the key, the user name and the password never show, and the polls and unchanged answers are routine.
- `Vm204Json` reads the answers strictly (anything else is a `Vm204ProtocolException` and counts as a failed poll). `Vm204Board` (4 relays, 4 inputs, 1 analog input, doesn't report changes), `Vm204Connection`, `Vm204Traffic` and `Vm204Model` (id `vm204`, port 80) are its adapters, `Features/Vm204` registers them.
- `Vm204Simulator`: a small HTTP server on `TcpListener` with the same routes and 400s as the firmware, an optional login, relays and inputs set at the board, the analog value, a new key, and refused or dropped connections. The simulator's page offers port 8204 for a new one (`SimulatorModel.SuggestedPort`), as 80 needs rights a developer doesn't have.
- To check on a real board: the status JSON as above, whether an input reads `true` closed or open, that the status right after a command already shows it, logins with symbols and the 48-character total (the firmware's `authUser` also reads past the end of the query), the key's length and characters (its buffer is 32 bytes and the terminator `gen_random` writes overruns it), that `relay/on` and `relay/off` answer properly (their handler in `httpServerUriNotFoundCallback` has a missing return), and days of polling every 2 s.

## The VM208 (`Contactor.Boards.Vm208`)

The VM208 (ESP32, WLAN or Ethernet) is neither the VM201's nor the VM204's protocol. Source: Velleman's firmware, https://github.com/Velleman/VM208 (`src/Network/server_VM208.cpp`, ESPAsyncWebServer, ArduinoJson 5), and its library https://github.com/velleman/VM208INT; each route names its firmware file in `Vm208Api`. Plain HTTP on port 80, JSON answers, one request per connection. It never reports a change by itself, so the app polls. Only the card itself is used (`interface=-1`); VM208EX extension modules are left out for now.

- `GET /status?interface=-1&socket=0` → `{"Channels":[{"name":"…","state":b,"pulseActive":b,"timerActive":b} ×4],"m1":b,"m2":b,"input":b}`. The firmware answers only with exactly two parameters and reads them by position; `{"Channels":[]}` when it couldn't take its I/O lock within 1 s (a failed poll).
- `POST /relay`, form `index=N&state=0|1&interface=-1&socket=0` → the same status. One relay per request: a mask is sent as one request per relay, all of them even when one answer can't be read (an error, `{"Channels":[]}`, broken JSON), and the last readable answer is the state, with a read at once to confirm the rest. When no relay's answer can be read (always so for a single relay), the command throws "The VM208 didn't take the command: …", as the VM204 does, so nothing counts as switched. A request that gets no answer at all stops the rest and counts as a failed poll. Either way the board is read at once. Read by position: a missing parameter or a relay outside 1–4 crashes the board, so the app only ever sends all four, with relays 1–4.
- No login: HTTP basic auth protects only the board's own pages, never `/status` or `/relay`, so `Vm208Model` has login length 0 and `Vm208Connection` takes no login but none. `m1`/`m2` are the two transistor outputs, not relays: read and traced, never counted.
- `Vm208Client`: `HttpClient` (no proxy, answers capped at 8 KiB since relay names have no length limit, 5 s per request on `TimeProvider`), polling through `BoardPoller` (`Vm208:PollSeconds`, default 2). `Vm208Json` reads the answer strictly where the app uses it (each `state`, `input`), after UTF-8 decoding with replacement, so a broken name can't make every status unreadable. `Vm208Board` (4 relays, 1 input, doesn't report changes), `Vm208Connection`, `Vm208Traffic` and `Vm208Model` (id `vm208`, port 80) are its adapters, `Features/Vm208` registers them.
- `Vm208Simulator`: a small HTTP server on `TcpListener` with the same routes, relays and the input set at the board, refused or dropped connections, and no answer where the firmware gives none or would crash. The simulator's page offers port 8208.
- The board's own schedules, pulses and timers compete with the app's and are left as they are (the firmware has `POST /setalarm` and `/shedule_set` that could turn the schedules off).
- To check on a real board: the status JSON as above, whether the input reads `true` closed or open (it is the raw pin level), that the answer to `/relay` already shows the change (if the firmware can't take its lock within 100 ms the relay isn't switched), that switching a relay stops its pulse or timer, that the board's own schedules and buttons show on the next poll, how it copes with polling every 2 s for days, and `/status` with an extension module plugged in.

## The simulator (`Contactor.Simulator`)

A small Blazor web app for development, laid out for a desktop browser and filling the window; not for phones. English, like the app.
- `Simulator:Boards` lists the boards that start with it (`Type`, `Port`, optional `Username` and `Password`); none listed: one VM201 on 9760. Boards added on the page last until it stops. Each board listens on its own port on 127.0.0.1, so several run at once.
- `SimulatorCatalogue`: the models it can simulate, each with its `ISimulatedBoard`. A new model is one line there.
- `SimulatedBoards` runs them; `RunningBoard` keeps each board's latest 100 traffic lines in memory, with the keep-alive left out and counted, and logs what is done on the page as "at the board".
- The page: the boards on the left with "Add a board"; the selected board's relays (a click switches one as the board's own button would), input, own timers, "Drop connection" and "Remove"; its traffic as a table below (time, direction, what, bytes; errors red).

---

## Data model (SQLite)

```
Relay
  Id              int PK         1 to the board's relay count (at most 64), seeded at start, never deleted
  Name            text           e.g. "Fan heater – Lathe" (max 40 characters)
  IsActive        bool           false = hidden from the list by default
  MaxOnMinutes    int?           auto-off after a manual on; null = never. Default 120.
  SortOrder       int

Schedule                (a relay's or a group's: exactly one of RelayId and GroupId is set)
  Id              int PK
  RelayId         int? FK
  GroupId         int? FK        cascade delete with the group
  Days            int            bit mask: Mon=1, Tue=2, Wed=4, Thu=8, Fri=16, Sat=32, Sun=64
  OnTime          text "HH:mm"   LOCAL time (Europe/Stockholm)
  OffTime         text "HH:mm"   LOCAL time; OffTime < OnTime = over midnight
  IsEnabled       bool
  CreatedBy       text           e-mail

User
  Email           text PK        lower case
  DisplayName     text           "John", "Jane" … shown in the app and in Events
  ShowBoardTraffic bool          the person's Debugging setting: toasts with the board's traffic and the Debug tab

EventLog
  Id              int PK
  TimestampUtc    datetime
  RelayId         int?           null for events that don't concern one relay
  Actor           text           e-mail, or "schedule" | "auto-off" | "external" | "system"
  Action          text           "on" | "off" | "rename" | "activate" | "deactivate" |
                                 "schedule-add" | "schedule-edit" | "schedule-delete" |
                                 "connection-lost" | "connection-restored" | "input-changed" |
                                 "board-timers-disabled" | "board-address-changed" | "board-login-changed"
  GroupId         int?           the group it concerns; not a foreign key, events outlive a deleted group
                                 Group actions: "group-on" | "group-off" | "group-start-failed" | "run-on-start" |
                                 "run-on-end" | "interlock-trip" | "group-create" | "group-edit" | "group-delete"
  Details         text?          JSON, e.g. {"from":"Relay 4","to":"Ventilation – Paint shop"}

RelayRuntime            (internal, for auto-off; one row per relay, seeded at start)
  RelayId         int PK
  OnSinceUtc      datetime?
  AutoOffAtUtc    datetime?
  TurnedOnBy      text?          e-mail, or "schedule"

SchedulerState          (internal, one row: the scheduler's progress)
  Id              int PK = 1
  LastTickUtc     datetime?      the last minute the scheduler handled

BoardSetting            (one row; the board's address from the Settings screen)
  Id              int PK = 1
  Host            text           IP address or host name
  Port            int            1–65535
  Username        text?          the board's login; null while Vm201:Username/Password apply
  Password        text?          kept as typed (the board needs it); never logged or shown
                                 While there is no row, Vm201:Host/Port/Username/Password from the settings
                                 (install.sh) apply; once saved in the app, the row wins.

RelayGroup              (two or more relays that run as one, in a start order)
  Id              int PK
  Name            text           max 40
  Icon            text           "fan" | "flame" | "bulb" | "wind" | "spray" | "drop" | "wrench" | "bolt"
  MaxOnMinutes    int?           auto-off for the whole group; null = never
  IsActive        bool
  SortOrder       int

RelayGroupMember
  GroupId         int FK         cascade delete
  RelayId         int FK UNIQUE  a relay is in at most one group
  Position        int            1..n, unique per group; 1 starts first and stops last
  RoleName        text           its name in the group, e.g. "Fan"
  StartDelaySec   int            1–60 (default 3): wait after the relay above is confirmed on
  RunOnSec        int            0–1800 (default 0): keeps running after the relay below has gone off

GroupRuntime            (internal; the sequence in progress, so it carries on after a restart)
  GroupId         int PK
  Phase           text           "idle" | "starting" | "stopping"
  TargetLevel     int            how many relays from the top the sequence heads for
  NextStepAtUtc   datetime?      when the next step may go (start delay or run-on)
  PendingPosition int?           a relay a command was sent for, not yet confirmed by the board
  PendingOn       bool
  PendingSinceUtc datetime?
  OnSinceUtc, AutoOffAtUtc, TurnedOnBy   as in RelayRuntime, for the group

Installation            (one row; none on a new installation)
  Id              int PK = 1
  BoardType       text?          the model chosen in the setup guide (catalogue id); wins over Board:Type
  SetupDoneUtc    datetime?      when the setup guide was finished or skipped; null: "/" opens on the guide
                                 The migration that added it marked installations already in use as done.

Input                   (future; create the table now but show nothing in the UI)
  Id              int PK = 1
  Name            text
  ClosedText      text   "Closed"
  OpenText        text   "Open"
  NotifyOnOpen    bool
  NotifyOnClose   bool
```

- `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout=5000` at start.
- **SQLite stays the database.** Keep SQLite-specific code to `Persistence` (the start-up settings above) and `Features/Backup` (`VACUUM INTO`); everything else goes through EF Core, so a later switch to PostgreSQL stays a small change.
- **Relay state is not stored.** The VM201 is the source of truth; the app always shows the board's latest `CMD_STATUS`.
- Every timestamp is stored in UTC and shown in Europe/Stockholm. Schedules are stored as local time, see below.
- EF Core migrations: `dotnet tool run dotnet-ef migrations add <Name> --project Contactor.Web --context ContactorDbContext --output-dir Persistence/Migrations --namespace Contactor.Web.Persistence.Migrations`. The tool puts the model snapshot in a wrong folder named after the namespace; move it to `Persistence/Migrations/`. The database file is `/data/contactor.db` in the container (`/var/lib/contactor` on the Raspberry Pi's SD card). The app writes little (a row a minute from the scheduler, the event log, a nightly backup), so a good SD card is enough; an endurance card is recommended.

---

## Domain rules

### Manual control
- The ON/OFF button sends `CMD_ON`/`CMD_OFF` with a single bit and logs the event with the user's e-mail.
- On a manual ON with `MaxOnMinutes` set: set `RelayRuntime.AutoOffAtUtc`.
- **"All off"** sends `CMD_OFF 0xFF` and is logged as one event per relay that was actually on.
- While the board is disconnected the buttons are disabled. Never queue commands for later.

### Auto-off
- A `BackgroundService` checks every 15 seconds. When `AutoOffAtUtc` has passed and the relay is still on, it sends OFF and logs the event with Actor `auto-off` and Details `{"afterMinutes":120}`.
- Does **not** apply to relays turned on by a schedule. There the schedule's OffTime is the end.

### Scheduling
- A `BackgroundService` ticks every minute, on the minute.
- Times are read in `TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")`. **Daylight saving:** an OnTime that doesn't exist (02:xx in spring) runs at the next valid minute. An OnTime that exists twice (02:xx in autumn) runs only once.
- At OnTime ON is sent, and at OffTime OFF. Both are logged with Actor `schedule`.
- A window over midnight (OffTime < OnTime) belongs to the day it started.
- **At start or reconnect:** reconcile against the schedules. If an active schedule window is running and the relay is off, turn it on. Never turn anything *off* at start, with two exceptions: auto-off times that have already passed, and a schedule's missed OffTime for a relay **that schedule turned on** (see below).
- If two schedules overlap: ON wins (an OFF is skipped while another of the relay's schedules is running).
- **Catch-up:** the scheduler stores the last minute it handled (`SchedulerState.LastTickUtc`). After a restart or a lost connection it handles what it missed, at most 24 h back: a window that is still running is turned on, and a missed OFF is applied only if `RelayRuntime.TurnedOnBy` is `schedule`. A relay a person turned on is never turned off at start. A live tick (OffTime within the last minute) turns the relay off whoever turned it on. While the board is disconnected the scheduler does nothing and doesn't move `LastTickUtc`.
- A relay the scheduler turned on is turned off at the next tick when no running window covers it any more (its schedule was deleted, paused or moved during the run, or its OFF is older than the catch-up reaches): nothing else would turn it off.
- **A person's auto-off wins over a schedule.** A relay a person turned on keeps their `AutoOffAtUtc` even when a schedule window starts while it is on. When auto-off turns it off, the schedule doesn't turn it back on during that run; it runs again at its next OnTime. A new "Turn off automatically after" limit never gives a relay the schedule turned on an auto-off.
- A relay the scheduler turns on gets `RelayRuntime.TurnedOnBy = "schedule"` and no `AutoOffAtUtc`; its status line reads "On since 06:30 · schedule · off 08:00". An off relay whose schedule turns it on within 24 h reads "Schedule: on tomorrow 06:30".

### Relay groups
A group runs its relays in a start order, e.g. a fan heater: the fan first, then the heat; heat off, the fan runs on, then the fan off. `GroupPlanner` has the rules (pure, tested without a board); `GroupSequencer` runs them on every status and every second, and saves `GroupRuntime`.
- **Start:** from the top. The next relay only once the board's status confirms the one above is on **and** its `StartDelaySec` has passed. No confirmation within 5 s: the start stops, what runs keeps running, `group-start-failed`.
- **Stop:** from the bottom. The lowest relay goes off at once; each one above after its `RunOnSec`, counted from when the one below is confirmed off. ON during a run-on carries on from where the group is, restarting nothing.
- **Interlock:** a relay may only be on while every relay above it is. Checked first on **every** status, whatever caused it (the app, the board's web page or buttons, its timers, a power cut), and at start and reconnect: everything below the gap goes off at once, the missing relay is started so it can run on, then the group stops as usual. Logged as `interlock-trip` with Actor `system`.
- **One relay at a time** (on the Group screen): ON only when the relay above is on ("Needs Fan to be on"). OFF turns the relays below off at once, then this one runs on and stops. That gives "only the fan" without a special mode.
- A relay in a group is only switched through its group (`RelayControl` refuses), has no auto-off or schedule of its own, and can't get a schedule while in it (its group can). A relay with its own schedules can't join a group. Which relays and their order, creating and deleting only change while the whole group is off.
- **Schedules:** a group has schedules of its own, run by the scheduler by the same rules as a relay's (catch-up, ON wins on overlap, a missed OFF only stops what the schedule started, a person's start and auto-off win). OnTime starts the group in its order through `GroupSequencer`, with Actor `schedule` and no auto-off; OffTime stops it with its run-on times. The sequencer logs both as `group-on`/`group-off`. A group that is already partly on at OnTime (e.g. only the fan, switched by hand) is left as the person set it, with their auto-off; the OffTime still stops it.
- **Auto-off** (`RelayGroup.MaxOnMinutes`) stops the whole group with its run-on times. **"All off"** stops each group with its run-on times and turns the other relays off at once.
- **No board:** nothing is sent or queued; run-on times keep counting, and on reconnect the interlock goes first, then what fell due. A command the board hadn't confirmed gets a fresh 5 s to show; the relay a stop needs for its run-on (e.g. the fan after a trip) is sent once more, then given up (`group-start-failed`) if it still doesn't come on.
- **Safety:** the app's interlock only works while the Pi and the app run. Where safety depends on it (heat without the fan), it must also be wired in hardware, e.g. by feeding the heater relay through the fan relay's output. That is the electrician's decision; the app is an extra layer.

### The board's own timers
- The board's built-in timer programs compete with the app's schedules. On the first successful connection: send `CMD_TMR_DIS 0xFF` and log it with Actor `system`. Configurable through `Vm201:DisableBoardTimers=true`.

### External changes
- If `CMD_STATUS` shows a change the app didn't ask for (through the board's web GUI, the physical button or timers): log it with Actor `external`.
  - Implementation: keep a short list of "expected changes" that is cleared after 5 s.
  - A command that fails (refused, unreadable or no answer) logs nothing, and drops the expectation it added (`ExpectedChanges.Forget`), so a relay the board switched anyway shows on the next status as an external change instead of being taken for the app's own and lost. That holds for single relays, "All off", groups, auto-off and schedules alike.

### Connection
- A new installation may have no address for its board (`Vm201:Host` empty): nothing connects, and no contact is lost, until one is saved (in the setup guide or on Settings). `BoardHostedService` waits for it.
- When contact is lost for more than 30 s, `connection-lost` is logged, and `connection-restored` when it comes back.
- The UI shows a red warning: "No contact with the relay board. Since HH:mm. The states below may be wrong."

---

## Identity (Cloudflare Access)

- **Validate `Cf-Access-Jwt-Assertion`** on every request (including static files and Blazor's SignalR connection): RS256, `aud` against the Access application's AUD tag, `iss` against `https://<TeamDomain>`, lifetime against `TimeProvider`. Missing or invalid JWT: answer 403 "Access denied.". Configured under `CloudflareAccess:TeamDomain` (the host name only) and `CloudflareAccess:Audience`; the app doesn't start in Production without them.
- **The e-mail comes from the JWT's `email` claim**, not from the `Cf-Access-Authenticated-User-Email` header: the claim is signed, the header isn't.
- The JWKS is fetched from `https://<TeamDomain>/cdn-cgi/access/certs` and cached for 1 h. A JWT with an unknown key refetches the list at once, at most once a minute. A failed fetch keeps the keys it had; without keys everyone is denied.
- `/healthz` needs no JWT (it answers localhost only), and in Development neither do `/health` and `/alive`.
- Unknown e-mail, i.e. let in by Access but missing from `User`: create a `User` automatically with DisplayName = the part before @, with a capital first letter.
- **Development:** `CloudflareAccess:DevUserEmail` instead of the header. Must never be active in Production; throw at start if it is set there.
- Blazor Server: the middleware sets `HttpContext.User`, which Blazor carries into the circuit when it starts. Components read the user through the scoped `CurrentUser` (built on `AuthenticationStateProvider`), since headers are gone in the SignalR circuit afterwards.

---

## User interface

Mobile first (390 px), and it uses the space on a tablet and a desktop. All UI text is in **English**. Add a PWA manifest so it can be put on the home screen with an icon and the name "Contactor".

**Three layouts**, by width only (Tailwind's `md` and `lg`), in each screen's `.razor.css` and `app.css`:
- **Phone, under 768 px:** one column, the tab bar at the bottom.
- **Tablet, 768 px and up:** wider screens. On Relays the relays are in two columns, and the groups one per row. The tab bar stays at the bottom.
- **Desktop, 1024 px and up** (a tablet in landscape too): the tab bar becomes a side menu on the left (`TabBar.razor.css`; pages with it get `padding-left: var(--side-menu)` through `body:has(nav.tabs)`, so the side menu is only used where the browser supports `:has`; older browsers keep the bottom tab bar). On Relays the relays have two columns, three from 1280 px, and from 1280 px two groups sit side by side. Relay and Group have two columns, the events on the right, and Save at the top right, pinned while the page scrolls (a second submit button in the header, `form="…"`; the bottom bar keeps only its warnings). On a tablet Events and Debug go up to 960 px wide and Settings to 720 px; on a desktop every page with the side menu starts right beside it, is up to 1200 px wide, lined up with Relays, and Settings has its cards in two columns. On a desktop "New group" sits in the Relays header beside "All off". The pages under a tab (Relay, Group, a schedule, New group) show the side menu too (`TabBar` with `SideMenuOnly`, no bottom tab bar on a phone). Forms (schedule, new group) stay narrow: 560 px on a tablet, and on a desktop a 640 px card beside the side menu, with their buttons at the bottom of the card.
- **Mouse and keyboard:** hover states and a visible `:focus-visible` ring on buttons and links (inputs keep the browser's own). Touch targets stay at least 44 px. The no-zoom rules apply on every width.

**No zoom.** The pages don't zoom, so nobody zooms in by mistake and loses the big buttons: `user-scalable=no` in the viewport (Android's Chrome follows it), `touch-action: manipulation` against double-tap zoom, and `wwwroot/phone.js`, which cancels the two-finger pinch on iOS, where every browser ignores `user-scalable=no`. Anyone who needs to enlarge can use the phone's own zoom (on iOS: Settings → Accessibility → Zoom).

**Like an app on the phone:**
- No grey tap flash; buttons show a pressed state, and a long press selects no text and opens no "Copy" menu.
- A short buzz on ON/OFF and "All off" (`data-vibrate`, `phone.js`; Android only, iOS gives web pages no vibration).
- The manifest locks the installed app to portrait (Android) and has a fixed `id`. `viewport-fit=cover`, with the content kept clear of the notch and the home indicator.
- **Reconnecting:** when the live connection drops (the phone slept, an update restarted the Pi), a bar in the app's style replaces Blazor's own dialog: "Reconnecting…", and "No connection to the app." with "Try again" when it gives up; it tries again by itself when the page is looked at. A page the server no longer knows reloads by itself (`wwwroot/reconnect.js`).
- `phone.js` and `reconnect.js` are the app's only JavaScript of its own.

### Screens

1. **Relays** (`/`)
   - Heading "Contactor", "Hi {DisplayName} · {number on} of {number active} on" and the button "All off".
   - Connection line: a green dot with "Board online · updated just now", or a red warning box.
   - One card per active relay: a small mono label "RELAY n", the name and a status line, plus a big ON/OFF button (84×52).
     - Status line when on: "On since 14:02 · Jane · off 16:02".
     - Status line when off: "Off", "Schedule: on tomorrow 06:30" or "Off since yesterday · Alex".
   - The link "New group" under the cards, then the button "Show N inactive relays" / "Hide inactive relays". Inactive relays have a dashed border.
   - With groups, the list has two sections, each with a small heading: "Groups" first, then "Relays", each in a grid of its own, so the cards line up and Tab goes row by row. Without groups there are no headings, and without a relay card to show (every relay in a group, or the rest hidden as inactive) there is no "Relays" heading.
   - One card per group, before the relays: its icon, "GROUP · RELAY 1 → 2", the name, a status line ("Starting · Heat in 3 s", "Starting · step 2 of 2", "Stopping · Fan keeps running for 4 min 59 s" (a countdown, every second; "42 s" under a minute), "On since 14:02 · John · off 16:02 · only Fan", "Schedule: on tomorrow 06:30") and one ON/OFF for the whole group, with a chip per relay in start order. A group's relays get no card of their own, and the counts in the heading count a group as one.
   - Tab bar at the bottom: Relays | Events | Settings, plus Debug for a person who has Debugging on.
2. **Relay** (`/relay/{id}`)
   - A name field and the checkbox "Active: shown in the list".
   - A status card with a wide TURN OFF / TURN ON button.
   - "Turn off automatically after": 1 h / 2 h / 4 h / Never.
   - "Schedules": a list with an Active/Paused button per schedule, and "Add".
   - "Events": the latest 4 for the relay and the link "All events".
   - A relay in a group shows "This relay is in the group X and is switched from there" and "Open the group" in place of its status, auto-off and schedules.
   - The button "Save".
3. **New/edit schedule** (`/relay/{id}/schedule/new`, `/group/{id}/schedule/new`, `/schedule/{id}`)
   - Day buttons Mo Tu We Th Fr Sa Su plus "Weekdays" and "Every day".
   - The time fields "Turn on" and "Turn off".
   - A plain-language summary: "Monday to Friday: the relay turns on at 06:30 and off at 08:00."
   - The button "Save schedule". When editing also "Delete schedule", with a confirmation.
4. **Group** (`/group/{id}`)
   - The name, the icon, a status card with a wide TURN ON THE GROUP / TURN OFF THE GROUP button.
   - "Relays in start order": each relay with its number, role name, state ("Needs Fan to be on", "Starts in 3 s", "Keeps running for 4 min 59 s"), its own ON/OFF within the interlock, up/down and "Remove from the group"; between two relays "On start, wait" (seconds) and "On stop, run on". Adding a relay offers only relays in no group and without schedules. Order and members only change while the group is off.
   - "Turn off automatically after", "Schedules" (as on the Relay screen, "Add" goes to `/group/{id}/schedule/new`), the group's latest 4 events, the safety note about wiring the interlock in hardware, "Delete the group" with a confirmation, and "Save".
   - Icons are Font Awesome Free solid icons (CC BY 4.0), inlined as SVG in `GroupIcon.razor`; no icon font or script.
5. **New group** (`/group/new`): a guide in 4 steps, "STEP n OF 4" with a progress bar, "Cancel" on the first step and "Back" after.
   1. "Name and icon": the name and the 8 icons (Fan, Heat, Light, Air, Paint, Pump, Tools, Power).
   2. "Which relays?": all the board's relays; the order they are chosen in (#1, #2 …) is the start order. A relay in another group ("Already in X") or with schedules of its own ("Has its own schedules") can't be chosen. At least 2.
   3. "Order and rules": up/down, "Name in the group" for each relay (its name to begin with) and the waits between the steps.
   4. "Review and create": the card as it will look, and in plain words when it starts, when it stops, the interlock and the auto-off (2 h, changed on the group's page). "Create group" only works while every chosen relay is off.
6. **Events** (`/events`)
   - Filter: All / People / Automatic.
   - Grouped by day (TODAY, YESTERDAY, date).
   - Row: time in mono, a round badge with an initial, and text.
     - People: a filled badge with their initial.
     - Schedule/auto/system: a dashed badge with "A".
     - Group events name the group: "John turned on the group Fan heater · Fan → 3 s → Heat", "Run-on: Fan keeps running for 5 min". A safety shut-off ("Heat turned off because Fan was off") and a start that failed show as a red row.
   - 50 at a time, with the button "Show more" for the next 50 (a button rather than loading on scroll, so no JavaScript is needed).
7. **Settings** (`/settings`)
   - "Relay board": the model ("Velleman VM201 · 8 relays"), the board's address ("Address") and "Port" (the model's default port as its placeholder), its login, for a model that has one ("The board has no login", or "User name" and "Password", with the model's login hint, e.g. "At most 8 characters: letters a–z, digits and plain symbols.", or else "At most N characters."; the password is never shown, an empty field keeps it), the live status ("Board online at 192.168.1.50:9760", "No contact with the relay board at …" or "The relay board has no address yet"), and "Save". Saving moves the connection at once and is logged ("John changed the relay board's address from … to …", "John changed the relay board's login", never the values).
   - "People": everyone who has signed in, by e-mail, with their name ("Alex.smith" → "Alex") and "Save".
   - "Backups": the newest nightly copy ("Latest: 02:30 · 68 kB"), "Make a copy now" and "Download" (`GET /settings/backup`, behind Access): the newest copy as a zip (`contactor-YYYY-MM-DD.zip` with the `.db` inside), so browsers don't warn about a `.db` file. The copies on the Pi stay plain `.db` files.
   - "Debugging": the checkbox "Show the traffic to the relay board", per person and saved at once. While it is on, that person sees each packet to and from the board as a toast at the bottom of every page (at most 3, 6 s each, errors red; not the keep-alive or the channel names), and gets a fourth tab, Debug.
   - The running version at the bottom ("Version 2026.11.4 · 1fdaf34", "Development version" for local builds; `AppBuild.Text`); the only place it shows, except on a desktop, where it sits at the bottom of the side menu instead.
8. **Debug** (`/debug`; the tab only shows while the person has Debugging on)
   - The latest 100 steps on the board connection, newest first, in memory only: time, → / ←, the step in plain words ("Turn on relay 3", "Status: relay 1 and 3 on · input off", "Connection lost: …") and the packet's bytes. The button "Clear".
   - Routine steps (the VM201's keep-alive and channel names, the VM204's and VM208's polls and answers that change nothing) are only counted (`BoardTrace.RoutineLeftOut`): "N routine steps left out, such as status checks that changed nothing", kept current at most once a second (`RoutineCounted`), so a board that is asked every 2 s can't push the rest out of the list.

9. **Setup** (`/setup`, `SetupGuide.razor`, `Features/Setup`): a new installation opens on it ("/" redirects there) until someone finishes or skips it; anyone signed in may go through it. No tab bar; "STEP n OF 5" with a progress bar, "Skip for now" on the first step (marks it done) and "Back" after.
   1. "Welcome to Contactor": "Your name" (saved as the person's DisplayName) and "YOU'LL NEED".
   2. "Which relay board?": the models in the catalogue with what they have ("8 relays · 1 input"). Another model than the running one is saved and the app restarts once (`AppRestart` stops it; compose starts it again), and the page comes back at `/setup?step=3`.
   3. "Where is the board?": address, port (empty: the model's default) and login, saved through `BoardAddressHandler`; "Next" waits up to 10 s for the board (`BoardContact`, which ignores a connection to the old address) and shows "Board online" or "No answer from …". "Go on without a connection" saves what was typed and goes on.
   4. "Name your relays": a name ("Relay n" as its hint) and "In use" for each relay, saved as on the Relay screen. "Try" turns a relay on for 2 s through `RelayControl` (refused for a relay in a group), off again even if the page closes.
   5. "All set, {name}": the board and "6 in use, 2 hidden", links to "Make a group" and a relay's page for a schedule, and "Go to Relays". Reaching it marks the guide done.

Blazor Server gives live updates for free. When the board reports a new state (`IRelayBoard.StateChanged`), every open page must update at once, through a singleton service with an event that components subscribe to and unsubscribe from in `Dispose`.

### Design tokens

```css
--bg: #F5F2EC;        /* background */
--card: #FFFFFF;
--ink: #1D1B18;       /* text */
--muted: #5C574F;     /* secondary text, ≥4.5:1 against --bg */
--line: #E3DED5;      /* card border */
--line-strong: #CFC8BC; /* input border */
--chip: #E8E3DA;      /* OFF button, badges */
--accent: #C2410C;    /* ON, links, primary action */
--accent-tint: #FFF4EC; /* card when the relay is on */
--accent-border: #F0BE9C;
--ok: #2F7D4F;
--danger-bg: #FDECEA; --danger-border: #E5A29B; --danger-ink: #7A1A10;
font: "IBM Plex Sans" (UI), "IBM Plex Mono" (times, labels)
radii: 10–14 px. Touch targets ≥ 44 px. Body text 16 px, relay names 18 px, heading 28 px.
```

**Dark mode** follows the phone's setting (`prefers-color-scheme: dark`); the app has no switch. `app.css` redefines the tokens (warm near-black `--bg: #16140F`, `--card: #221F1A`, `--ink: #F2EEE7`, `--muted: #B3AA9E`, `--accent: #F06A2C` …), plus a few named for it: `--on-ink` (text on an `--ink` background), `--on-accent`, `--danger-solid` (the "Delete" button), `--row-line`, `--line-dashed`, `--inactive-bg`/`--inactive-border` and `--backdrop`. Pages take every colour from a token, never a hex value of their own (a test checks), so a new page is dark too.

Use real `<button>` and `<label>` elements, and `aria-pressed` on the ON/OFF buttons. No emojis and no gradients.

---

## Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": { "Default": "Data Source=/data/contactor.db" },
  "Board": { "Type": "vm201" },     // which model, from the board catalogue; vm201 when not set
  "Vm201": {
    "Host": "",                     // the board's address, e.g. 192.168.1.50; install.sh asks for it. Empty (without install.sh): the setup guide asks
    "Port": 9760,
    "Username": "",
    "Password": "",
    "DisableBoardTimers": true,
    "KeepAliveSeconds": 15,
    "LogPackets": false
  },
  "Vm204": {                        // only read with Board:Type vm204
    "Host": "",
    "Port": 80,
    "Username": "",
    "Password": "",
    "PollSeconds": 2
  },
  "Vm208": {                        // only read with Board:Type vm208; the board's API has no login
    "Host": "",                     // the board's address; empty: the setup guide asks
    "Port": 80,
    "PollSeconds": 2
  },
  "CloudflareAccess": {
    "TeamDomain": "",               // e.g. myteam.cloudflareaccess.com; install.sh asks for it
    "Audience": "<AUD tag of the Access application>",
    "DevUserEmail": null
  },
  "Tunnel": { "Token": "" },        // the Cloudflare tunnel's token; empty: no cloudflared
  "TimeZone": "Europe/Stockholm"
}
```

No `Urls` in the settings: the .NET image listens on 8080 (`ASPNETCORE_HTTP_PORTS`), and in development Aspire picks the port. ASP.NET Core's data protection keys are kept in `keys/` next to the database, so they survive upgrades.

Anything specific to the installation or secret (the Access team domain and AUD tag, the board's address and password, the tunnel token) is never in the repository or the image. An install script asks for them once, saves them in `/opt/contactor/.env` (readable by root only), and compose passes them in as environment variables, e.g. `CloudflareAccess__Audience` and `Tunnel__Token`.

---

## Operations

See `DEPLOY.md` for the steps.

- **Versions:** `YYYY.M.D` in UTC without leading zeros, plus `.N` for a later release the same day (`scripts/release-version.sh`). A release is built with `-p:Version=<version>`; every other build is `0.0.0-dev` (`Directory.Build.props`). `AppBuild` reads the version and commit; they show in `/healthz`, the start log, the image label `org.opencontainers.image.version`, the upgrade's output and at the bottom of Settings.
- **Publish:** the Images workflow (started by hand) or `scripts/images-publish.sh` builds the solution and runs every test (a failure publishes nothing), builds and pushes the image from the `Dockerfile` with `docker buildx` (`scripts/images-build.sh`), tags it `YYYY.M.D[.N]`, `latest` and `sha-<commit>`, signs it with cosign by digest (`scripts/images-sign.sh`, keys in `deploy/cosign/`) and tags the commit `v<version>`.
- **Run:** Docker Compose on the Raspberry Pi (Raspberry Pi OS Lite 64-bit on an SD card, an endurance card recommended), from `/opt/contactor` with `compose.yml` and `.env`. One container, run as the image's user 1654; the Pi's `/var/lib/contactor` is mounted as `/data` (database, `backups/`, `keys/`) and owned by it. `cloudflared` is pinned by digest in the `Dockerfile` and runs a remotely managed tunnel: `<your hostname> → http://contactor:8080`. An installation whose `compose.yml` predates `/data` still finds its database in `/var/lib/contactor` and logs that `install.sh` should be run again (`PersistenceServiceCollectionExtensions.Resolve`).
- **Upgrade:** `contactor-upgrade` (`deploy/upgrade.sh`) only runs an image signed with one of the keys in its `COSIGN_KEYS`; a systemd timer runs it nightly between 03:00 and 04:00 (`deploy/autoupdate-install.sh`). After a healthy start it keeps the 3 newest versions of the app's image (`KEEP_VERSIONS`, never the running one) and removes older ones, so a 16 GB SD card doesn't fill up.
- **Migration:** run `Database.Migrate()` at start.
- **Backup:** every night at 02:30 the app copies the database with SQLite's `VACUUM INTO` into `backups/contactor-YYYY-MM-DD.db` and keeps the latest 14 (`Backup:Folder`, `Backup:Keep`, `Backup:At`).
- **Health check:** `/healthz` (localhost only, otherwise 404) returns 200 and the board's connection status (`boardConnected`, `boardLastContactUtc`). The image has no shell or curl; `dotnet /app/Contactor.Web.dll healthcheck` asks it and exits 0 or 1, and compose uses that.

---

## Testing

- **Codec:** encode/decode, checksum, the verified example (`02 06 4F 01 A8 03`), split and merged TCP packets, and an invalid checksum.
- **Client against the simulator:** login with and without auth, wrong password, pushed status, reconnect after a disconnect.
- **Scheduling:** with an injected `TimeProvider`. Test weekdays, over midnight, both daylight saving changes, overlaps and the reconciliation at start.
- **Auto-off:** that it triggers, and not for relays turned on by a schedule.
- **Identity:** valid and invalid JWT, wrong `aud`, and that the dev fallback is blocked in Production.
- **CI:** the Build workflow runs `dotnet build` and `dotnet test` on every pull request to `main`, on every merge into `main` and by hand, and shows the results as a "Tests" check (`dorny/test-reporter`). A new push to a pull request cancels its older run. Run the related tests locally before pushing.
- **While the Build workflow is disabled** (to save Actions minutes during heavy development): ignore the missing GitHub checks and don't bring them up. The gate is a full local run instead: a Release build and every test on a clean checkout of the pull request's latest commit, with the commit and the result in the pull request's description.

## Workflow

- The user drives. Never decide the next step yourself.
- Ask the user only what you can't decide yourself from the code, these rules and the project's goals. The most important goal is simplicity for people who aren't technical (see the top of this file). Don't ask about that.
- Before writing code, explain what you plan to implement (files, types, behaviour) and wait for the user's tweaks and approval.
- **Scope is locked at the approved plan.** Anything outside it becomes its own issue, never more work in the same pull request. Only the user widens the scope.
- On your own working branch, commit and push freely, and keep its pull request up to date. No permission is needed for that.
- Never commit or push to `main`, and never merge a pull request, without explicit permission from the user. Permission to merge is explicit only when the user tells you to merge and names the pull requests. If it is unclear whether the user will merge themselves, ask.
- Work in small, reviewable chunks: one branch and one pull request per chunk.
- Before every push, review your own diff as a hostile reviewer would and fix what you find, with tests: crashes, unhandled input (empty, huge, negative, malformed), leaked subscriptions and timers, races, the safety rules above, these conventions, and stale comments or docs. Never skip this to save time or tokens; keep scope small instead.
- **Independent review:** before a pull request is ready, a reviewer that did not write the code reviews its whole diff by the checklist in `.agents/review.md` (in Claude Code, the `code-reviewer` agent in `.claude/agents/`), knowing only the diff, the surrounding code and these rules, not the author's notes. Every bug, safety and security problem in the pull request's own changes, or caused or made worse by them, is fixed before the merge, with tests, and the reviewer checks the fixes once more. At most two review rounds: one full review, then one check of only the fixes. What is still open after that goes to the user as a decision, not a third round. What the reviewer finds outside the pull request's scope becomes its own issue. A finding the author disagrees with, or a judgement call, goes to the user. The remaining nitpicks of a pull request go together into one follow-up issue, "Nitpicks from the review of PR #N".
- A pull request is **ready to merge** only when all of these hold for its latest commit:
  - the independent review covers the latest commit, and nothing it found is left open;
  - every review thread is resolved, except a thread whose finding the user explicitly decided not to act on, with that decision replied on the thread;
  - it has no `needs attention` label;
  - GitHub shows every check done and green; while the Build workflow is disabled, the full local run (see Testing) on that commit passed instead, with the result in the pull request's description.
  Check all four yourself before saying a pull request is ready; never on an agent's word or an older review.
- A pull request with a finding or decision waiting on the user (a review finding the author disagrees with, or an open design question) gets the `needs attention` label. Remove the label once it's resolved.
- Help the user keep the work fast and cheap: say so as soon as work is queuing up (jobs waiting on each other, overlapping files that will need merging), and propose a better order when one uses fewer tokens or queues less. Run one heavy job at a time (builds, the full test run) unless the jobs are small and independent.
- Keep reports to the user short: only what they must decide or know.
- Every piece of work has a GitHub issue; create one first if it's missing. When work starts, open a **draft** pull request with "Fixes #N"; mark it ready for review when it's done. Always write "Issue #N" or "PR #N" (in chat, issues, pull requests and commits), never "#N" or a number alone, so issues and pull requests are never mixed up.
- Pull requests get a clear title and a **short description** that follows `.github/pull_request_template.md`. Not an essay. Write pull request text, issues and comments by the rules in `.agents/writing.md`.

### Branches
- `type/issue-short-description`: the type, the issue number, and a few words in lower case with hyphens, e.g. `feature/12-setup-guide`. Without an issue, leave the number out (`chore/upgrade-aspire`). No user name, no `claude/` and no random suffix.
- The types:
  - `feature/`: adding, removing or changing a feature, e.g. `feature/30-vm208`
  - `bugfix/`: a bug that isn't urgent, e.g. `bugfix/41-login-button-align`
  - `hotfix/`: an urgent, critical fix that goes straight to production, e.g. `hotfix/52-scheduler-crash`
  - `refactor/`: cleaning up or restructuring code without changing what it does, e.g. `refactor/clean-board-client`
  - `docs/`: documentation, e.g. `docs/update-readme`
  - `test/`: adding or updating tests, e.g. `test/scheduler-daylight-saving`
  - `chore/`: maintenance, dependencies or build settings, e.g. `chore/upgrade-aspire`
- One branch per pull request.
- A branch starts from the latest `main`. When it changes the same files or area as another open branch, it is stacked on that branch instead, and its pull request is chained to the other's (based on its branch, not `main`), so they merge in order.

## Build order

1. `Contactor.Boards.Vm201`: codec, simulator and client, with tests. **Test against the real VM201 before going further.**
2. Web shell: EF model and migration, `CurrentUser` middleware, the Relays screen with live status and ON/OFF.
3. Event log and the Events screen.
4. The Relay screen: name, active, auto-off.
5. Schedules and the scheduler.
6. PWA manifest, operations files and a README with installation steps.

## Conventions

- Nullable on, `TreatWarningsAsErrors` in Release.
- Time: inject `TimeProvider`. Never use `DateTime.Now` directly.
- Log with `ILogger` and structured fields. Never log the VM201 password.
- Everything in English (see Languages).
