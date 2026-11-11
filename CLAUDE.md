# Contactor – the relay app

Contactor is an open-source mobile web app for controlling the 8 relays on a **Velleman VM201** ethernet relay board from a phone, e.g. in a workshop, where the relays drive fan heaters, a compressor, ventilation, lighting and more. It started as the app for one workshop, run by a licensed electrician and a few friends.

The users are not technical. **Simplicity comes before features**: big buttons, Swedish text, no menus that aren't needed.

The screens are described under User interface below; the running app is the reference for layout, texts, colours and sizes.

---

## Languages

- **The website (all UI text) is in Swedish** for now: the first users are Swedish and not technical. Other languages may come later.
- **Everything else is in English:** code, comments, log messages, tests, commit messages, pull requests, issues, `README.md` and this file.

---

## Architecture

```
Phone ──HTTPS──▶ <your hostname>: Cloudflare Access (e-mail OTP) ──▶ Cloudflare Tunnel
                                                         │
                                         cloudflared on the Raspberry Pi (workshop)
                                                         │ http://contactor:8080 (compose network)
                                              Contactor.Web (Blazor Server, .NET 10)
                                                 │                    │
                                           SQLite (WAL)        TCP :9760 ──▶ VM201
```

- **No port is opened to the internet.** All access goes through Cloudflare Tunnel + Access on the installation's own hostname. Each installation has its own Access team domain (`<team>.cloudflareaccess.com`) and application; the AUD tag is in Zero Trust → Access controls → Applications → Edit → Additional settings.
- **Sign-in is handled entirely by Cloudflare Access** (one-time PIN by e-mail, session length 1 month, an allow list of e-mail addresses). The app has no sign-in of its own.
- The app runs in a container and listens on port 8080 on all of the container's addresses, so cloudflared reaches it on the compose network. The port is **never published** on the Raspberry Pi, so nobody on the LAN can get around Access.
- Give the Pi and the VM201 DHCP reservations in the router, so their addresses don't change.

## Stack and structure

- .NET 10 (LTS), C#, Blazor Server (interactive server rendering), EF Core + `Microsoft.EntityFrameworkCore.Sqlite`
- No JavaScript framework. No external CSS framework; one small stylesheet of our own, following the design tokens below.
- Target: a container image (`ghcr.io/gonace/contactor`, linux-arm64 and linux-x64) on Ubuntu chiseled-extra, which includes the time zone data the schedules need; run with Docker Compose on the Raspberry Pi.
- Local development with Aspire, pinned to 13.5.4 (13.6.x hangs at startup on Windows, aspire#20773). The AppHost starts the simulator on port 9760 and the web app against it.

No `src/` or `tests/` folders: every project sits directly in the repository root. All tests live in **one** test project, `Contactor.Tests`, with a folder per area (`Vm201/`, `Web/` …). Don't create more test projects.

```
Contactor.slnx
global.json                 # SDK version and test runner (Microsoft.Testing.Platform)
Directory.Build.props       # Shared build settings
Directory.Packages.props    # Central package management: every package version is here
Contactor.Vm201/          # Pure protocol library: packet codec, client, simulator. No dependencies on web/EF.
Contactor.Web/            # Blazor app, EF Core, background services, identity
  Persistence/              # DbContext, entities (one file per entity) and migrations; not a feature
  Features/<Feature>/       # One folder per feature
Contactor.Simulator/      # Console app that runs Vm201Simulator on a port (development)
Contactor.AppHost/        # Aspire: runs everything locally (dotnet run --project Contactor.AppHost)
Contactor.ServiceDefaults/ # Aspire defaults: OpenTelemetry, health checks, resilient HTTP clients
Contactor.Tests/          # The only test project
  Vm201/                    # Codec + client against the simulator
  Web/                      # Scheduling, auto-off, daylight saving, identity
deploy/
  compose.yml               # The app + cloudflared on the Raspberry Pi; no published ports
  install.sh                # Run on the Pi: settings to /opt/contactor/.env, data folder, ghcr.io login, first start
  upgrade.sh                # Pulls, checks the cosign signature, restarts, waits until healthy
  autoupdate-install.sh     # systemd timer: upgrade every night 03:00–04:00
  cosign/                   # Public signing keys and how to make them
scripts/
  release-version.sh        # YYYY.M.D[.N]
  images-publish.sh         # Publish and sign the image from your computer
  images-sign.sh            # cosign, by digest
DEPLOY.md                   # Installation steps
.github/workflows/build.yml # build + test (pull requests, merges to main, by hand)
.github/workflows/images.yml # publish and sign the image (by hand)
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

## The VM201 client (`Contactor.Vm201`)

- `Vm201PacketCodec`: `byte[] Encode(byte cmd, ReadOnlySpan<byte> data)` and an incremental decoder over a `PipeReader` or `Stream`.
- `Vm201Client`:
  - Holds one persistent connection. On failure: reconnect with exponential backoff (1 s → max 30 s).
  - Exposes `Vm201State` (Outputs byte, TimerMask, Input, `IsConnected`, `LastContactUtc`) and an event or `IObservable` when the status changes.
  - Commands (`TurnOnAsync(mask)`, `TurnOffAsync(mask)`, `RequestStatusAsync()`, `DisableBoardTimersAsync(mask)`) are serialized through a `Channel<T>`, so only one writer is used at a time.
  - Timeouts: connect 5 s, login 5 s.
  - `SetEndpoint` and `SetLogin` change the address and login while it runs: the current connection, attempt or wait ends at once. The web app applies the saved `BoardSetting` before the first connection. `Vm201Login` is a class, not a record, so its password can't leak through a generated `ToString`.
  - `Vm201:LogPackets` (off by default) logs every packet in and out as hex with its command letter (`Vm201PacketTrace`); user name and password packets only as their command and length. The board keeps no log of its own, so this is how to see what a real board does.
  - The `Traced` event reports every packet in and out and every connection step (connecting, lost and why, the wait before the next attempt, skipped bytes) as a `Vm201TraceEntry`. User name and password packets come cut to their first three bytes. The web app keeps the latest 100 in memory (`BoardTrace`) for the Debug screen.
- `Vm201Simulator`: a `TcpListener`-based fake that implements the whole protocol, optional auth, push on change and a way to simulate a disconnect. It is used in the tests and in development, where `Contactor.Simulator` runs it as its own process through Aspire. The web app has no built-in simulator: it always connects to `Vm201:Host`.

---

## Data model (SQLite)

```
Relay
  Id              int PK         1–8, seeded at start, never deleted
  Name            text           e.g. "Värmefläkt – Svarven" (max 40 characters)
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
  DisplayName     text           "John", "Jane" … shown in the app and in Händelser
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
  Details         text?          JSON, e.g. {"from":"Relä 4","to":"Ventilation – Lackrummet"}

RelayRuntime            (internal, for auto-off; one row per relay, seeded at start)
  RelayId         int PK
  OnSinceUtc      datetime?
  AutoOffAtUtc    datetime?
  TurnedOnBy      text?          e-mail, or "schedule"

SchedulerState          (internal, one row: the scheduler's progress)
  Id              int PK = 1
  LastTickUtc     datetime?      the last minute the scheduler handled

BoardSetting            (one row; the board's address from the Inställningar screen)
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
  RoleName        text           its name in the group, e.g. "Fläkt"
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

Input                   (future; create the table now but show nothing in the UI)
  Id              int PK = 1
  Name            text
  ClosedText      text   "Stängd"
  OpenText        text   "Öppen"
  NotifyOnOpen    bool
  NotifyOnClose   bool
```

- `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout=5000` at start.
- **Relay state is not stored.** The VM201 is the source of truth; the app always shows the board's latest `CMD_STATUS`.
- Every timestamp is stored in UTC and shown in Europe/Stockholm. Schedules are stored as local time, see below.
- EF Core migrations: `dotnet tool run dotnet-ef migrations add <Name> --project Contactor.Web --context ContactorDbContext --output-dir Persistence/Migrations --namespace Contactor.Web.Persistence.Migrations`. The tool puts the model snapshot in a wrong folder named after the namespace; move it to `Persistence/Migrations/`. The database file is `/var/lib/contactor/contactor.db`, on the Raspberry Pi's SD card. The app writes little (a row a minute from the scheduler, the event log, a nightly backup), so a good SD card is enough; an endurance card is recommended.

---

## Domain rules

### Manual control
- The PÅ/AV button sends `CMD_ON`/`CMD_OFF` with a single bit and logs the event with the user's e-mail.
- On a manual ON with `MaxOnMinutes` set: set `RelayRuntime.AutoOffAtUtc`.
- **"Alla av"** sends `CMD_OFF 0xFF` and is logged as one event per relay that was actually on.
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
- **A person's auto-off wins over a schedule.** A relay a person turned on keeps their `AutoOffAtUtc` even when a schedule window starts while it is on. When auto-off turns it off, the schedule doesn't turn it back on during that run; it runs again at its next OnTime. A new "Stäng av automatiskt efter" limit never gives a relay the schedule turned on an auto-off.
- A relay the scheduler turns on gets `RelayRuntime.TurnedOnBy = "schedule"` and no `AutoOffAtUtc`; its status line reads "På sedan 06:30 · schema · av 08:00". An off relay whose schedule turns it on within 24 h reads "Schema: på i morgon 06:30".

### Relay groups
A group runs its relays in a start order, e.g. a fan heater: the fan first, then the heat; heat off, the fan runs on, then the fan off. `GroupPlanner` has the rules (pure, tested without a board); `GroupSequencer` runs them on every status and every second, and saves `GroupRuntime`.
- **Start:** from the top. The next relay only once the board's status confirms the one above is on **and** its `StartDelaySec` has passed. No confirmation within 5 s: the start stops, what runs keeps running, `group-start-failed`.
- **Stop:** from the bottom. The lowest relay goes off at once; each one above after its `RunOnSec`, counted from when the one below is confirmed off. PÅ during a run-on carries on from where the group is, restarting nothing.
- **Interlock:** a relay may only be on while every relay above it is. Checked first on **every** status, whatever caused it (the app, the board's web page or buttons, its timers, a power cut), and at start and reconnect: everything below the gap goes off at once, the missing relay is started so it can run on, then the group stops as usual. Logged as `interlock-trip` with Actor `system`.
- **One relay at a time** (on the Grupp screen): ON only when the relay above is on ("Kräver att Fläkt är på"). OFF turns the relays below off at once, then this one runs on and stops. That gives "only the fan" without a special mode.
- A relay in a group is only switched through its group (`RelayControl` refuses), has no auto-off or schedule of its own, and can't get a schedule while in it (its group can). A relay with its own schedules can't join a group. Which relays and their order, creating and deleting only change while the whole group is off.
- **Schedules:** a group has schedules of its own, run by the scheduler by the same rules as a relay's (catch-up, ON wins on overlap, a missed OFF only stops what the schedule started, a person's start and auto-off win). OnTime starts the group in its order through `GroupSequencer`, with Actor `schedule` and no auto-off; OffTime stops it with its run-on times. The sequencer logs both as `group-on`/`group-off`. A group that is already partly on at OnTime (e.g. only the fan, switched by hand) is left as the person set it, with their auto-off; the OffTime still stops it.
- **Auto-off** (`RelayGroup.MaxOnMinutes`) stops the whole group with its run-on times. **"Alla av"** stops each group with its run-on times and turns the other relays off at once.
- **No board:** nothing is sent or queued; run-on times keep counting, and on reconnect the interlock goes first, then what fell due. A command the board hadn't confirmed gets a fresh 5 s to show; the relay a stop needs for its run-on (e.g. the fan after a trip) is sent once more, then given up (`group-start-failed`) if it still doesn't come on.
- **Safety:** the app's interlock only works while the Pi and the app run. Where safety depends on it (heat without the fan), it must also be wired in hardware, e.g. by feeding the heater relay through the fan relay's output. That is the electrician's decision; the app is an extra layer.

### The board's own timers
- The board's built-in timer programs compete with the app's schedules. On the first successful connection: send `CMD_TMR_DIS 0xFF` and log it with Actor `system`. Configurable through `Vm201:DisableBoardTimers=true`.

### External changes
- If `CMD_STATUS` shows a change the app didn't ask for (through the board's web GUI, the physical button or timers): log it with Actor `external`.
  - Implementation: keep a short list of "expected changes" that is cleared after 5 s.

### Connection
- When contact is lost for more than 30 s, `connection-lost` is logged, and `connection-restored` when it comes back.
- The UI shows a red warning: "Ingen kontakt med reläkortet sedan HH:mm. Lägena nedan kan vara fel."

---

## Identity (Cloudflare Access)

- **Validate `Cf-Access-Jwt-Assertion`** on every request (including static files and Blazor's SignalR connection): RS256, `aud` against the Access application's AUD tag, `iss` against `https://<TeamDomain>`, lifetime against `TimeProvider`. Missing or invalid JWT: answer 403 "Åtkomst nekad.". Configured under `CloudflareAccess:TeamDomain` (the host name only) and `CloudflareAccess:Audience`; the app doesn't start in Production without them.
- **The e-mail comes from the JWT's `email` claim**, not from the `Cf-Access-Authenticated-User-Email` header: the claim is signed, the header isn't.
- The JWKS is fetched from `https://<TeamDomain>/cdn-cgi/access/certs` and cached for 1 h. A JWT with an unknown key refetches the list at once, at most once a minute. A failed fetch keeps the keys it had; without keys everyone is denied.
- `/healthz` needs no JWT (it answers localhost only), and in Development neither do `/health` and `/alive`.
- Unknown e-mail, i.e. let in by Access but missing from `User`: create a `User` automatically with DisplayName = the part before @, with a capital first letter.
- **Development:** `CloudflareAccess:DevUserEmail` instead of the header. Must never be active in Production; throw at start if it is set there.
- Blazor Server: the middleware sets `HttpContext.User`, which Blazor carries into the circuit when it starts. Components read the user through the scoped `CurrentUser` (built on `AuthenticationStateProvider`), since headers are gone in the SignalR circuit afterwards.

---

## User interface

Mobile first (390 px), but it must work on a desktop. All UI text is in **Swedish**. Add a PWA manifest so it can be put on the home screen with an icon and the name "Contactor".

**No zoom.** The pages don't zoom, so nobody zooms in by mistake and loses the big buttons: `user-scalable=no` in the viewport (Android's Chrome follows it), `touch-action: manipulation` against double-tap zoom, and `wwwroot/phone.js`, which cancels the two-finger pinch on iOS, where every browser ignores `user-scalable=no`. Anyone who needs to enlarge can use the phone's own zoom (on iOS: Settings → Accessibility → Zoom).

**Like an app on the phone:**
- No grey tap flash; buttons show a pressed state, and a long press selects no text and opens no "Copy" menu.
- A short buzz on PÅ/AV and "Alla av" (`data-vibrate`, `phone.js`; Android only, iOS gives web pages no vibration).
- The manifest locks the installed app to portrait (Android) and has a fixed `id`. `viewport-fit=cover`, with the content kept clear of the notch and the home indicator.
- **Reconnecting:** when the live connection drops (the phone slept, an update restarted the Pi), a Swedish bar replaces Blazor's English messages: "Ansluter igen…", and "Ingen kontakt med appen." with "Försök igen" when it gives up; it tries again by itself when the page is looked at. A page the server no longer knows reloads by itself (`wwwroot/reconnect.js`).
- `phone.js` and `reconnect.js` are the app's only JavaScript of its own.

### Screens

1. **Reläer** (`/`)
   - Heading "Contactor", "Hej {DisplayName} · {number on} av {number active} på" and the button "Alla av".
   - Connection line: a green dot with "Kortet svarar · uppdaterat nyss", or a red warning box.
   - One card per active relay: a small mono label "RELÄ n", the name and a status line, plus a big PÅ/AV button (84×52).
     - Status line when on: "På sedan 14:02 · Jane · av 16:02".
     - Status line when off: "Av", "Schema: på i morgon 06:30" or "Av sedan i går · Alex".
   - The link "Ny grupp" under the cards, then the button "Visa N inaktiva reläer" / "Dölj inaktiva reläer". Inactive relays have a dashed border.
   - One card per group, before the relays: its icon, "GRUPP · RELÄ 1 → 2", the name, a status line ("Startar · steg 2 av 2", "Stänger av · Fläkt går 5 min till", "På sedan 14:02 · John · av 16:02 · bara Fläkt", "Schema: på i morgon 06:30") and one PÅ/AV for the whole group, with a chip per relay in start order. A group's relays get no card of their own, and the counts in the heading count a group as one.
   - Tab bar at the bottom: Reläer | Händelser | Inställningar, plus Debug for a person who has Debugging on.
2. **Relä** (`/rele/{id}`)
   - A name field and the checkbox "Aktiv – visas i listan".
   - A status card with a wide STÄNG AV / SLÅ PÅ button.
   - "Stäng av automatiskt efter": 1 h / 2 h / 4 h / Aldrig.
   - "Schema": a list with an Aktiv/Pausad button per schedule, and "Lägg till".
   - "Händelser": the latest 4 for the relay and the link "Alla händelser".
   - A relay in a group shows "Reläet ingår i gruppen X och styrs därifrån" and "Öppna gruppen" in place of its status, auto-off and schedules.
   - The button "Spara".
3. **New/edit schedule** (`/rele/{id}/schema/ny`, `/grupp/{id}/schema/ny`, `/schema/{id}`)
   - Day buttons M T O T F L S plus "Vardagar" and "Alla dagar".
   - The time fields "Slå på" and "Stäng av".
   - A plain-language summary: "Måndag till fredag: reläet slås på 06:30 och stängs av 08:00."
   - The button "Spara schema". When editing also "Ta bort schema", with a confirmation.
4. **Grupp** (`/grupp/{id}`)
   - The name, the icon, a status card with a wide SLÅ PÅ GRUPPEN / STÄNG AV GRUPPEN button.
   - "Reläer i startordning": each relay with its number, role name, state ("Kräver att Fläkt är på", "Går 5 min till"), its own PÅ/AV within the interlock, up/down and "Ta bort ur gruppen"; between two relays "Vid start, vänta" (seconds) and "Vid stopp, eftergång". Adding a relay offers only relays in no group and without schedules. Order and members only change while the group is off.
   - "Stäng av automatiskt efter", "Schema" (as on the Relä screen, "Lägg till" goes to `/grupp/{id}/schema/ny`), the group's latest 4 events, the safety note about wiring the interlock in hardware, "Ta bort gruppen" with a confirmation, and "Spara".
   - Icons are Font Awesome Free solid icons (CC BY 4.0), inlined as SVG in `GroupIcon.razor`; no icon font or script.
5. **Ny grupp** (`/grupp/ny`): a guide in 4 steps, "STEG n AV 4" with a progress bar, "Avbryt" on the first step and "Tillbaka" after.
   1. "Namn och ikon": the name and the 8 icons (Fläkt, Värme, Ljus, Luft, Lack, Pump, Verktyg, Ström).
   2. "Vilka reläer ingår?": all 8 relays; the order they are picked in (#1, #2 …) is the start order. A relay in another group ("Ingår redan i X") or with schedules of its own ("Har egna scheman") can't be picked. At least 2.
   3. "Ordning och regler": up/down, "Namn i gruppen" for each relay (its name to begin with) and the waits between the steps.
   4. "Granska och skapa": the card as it will look, and in plain words when it starts, when it stops, the interlock and the auto-off (2 h, changed on the group's page). "Skapa grupp" only works while every chosen relay is off.
6. **Händelser** (`/handelser`)
   - Filter: Alla / Personer / Automatiskt.
   - Grouped by day (IDAG, IGÅR, date).
   - Row: time in mono, a round badge with an initial, and text.
     - People: a filled badge with their initial.
     - Schedule/auto/system: a dashed badge with "A".
     - Group events name the group: "John slog på gruppen Värmefläkt · Fläkt → 3 s → Värme", "Eftergång: Fläkt går 5 min till". A safety shut-off ("Värme stängdes av eftersom Fläkt var av") and a start that failed show as a red row.
   - 50 at a time, with the button "Visa fler" for the next 50 (a button rather than loading on scroll, so no JavaScript is needed).
7. **Inställningar** (`/settings`; new URLs are English)
   - "Reläkortet": the board's address ("Adress") and "Port", its login ("Kortet har ingen inloggning", or "Användarnamn" and "Lösenord"; the password is never shown, an empty field keeps it), the live status ("Kortet svarar på 192.168.1.50:9760" or "Ingen kontakt med reläkortet på …"), and "Spara". Saving moves the connection at once and is logged ("John ändrade reläkortets adress från … till …", "John ändrade reläkortets inloggning", never the values).
   - "Personer": everyone who has signed in, by e-mail, with their name ("Alex.smith" → "Alex") and "Spara".
   - "Säkerhetskopior": the newest nightly copy ("Senaste: i natt 02:30 · 68 kB"), "Gör en kopia nu" and "Ladda ner" (`GET /settings/backup`, behind Access): the newest copy as a zip (`contactor-YYYY-MM-DD.zip` with the `.db` inside), so browsers don't warn about a `.db` file. The copies on the Pi stay plain `.db` files.
   - "Debugging": the checkbox "Visa trafiken till reläkortet", per person and saved at once. While it is on, that person sees each packet to and from the board as a toast at the bottom of every page (at most 3, 6 s each, errors red; not the keep-alive or the channel names), and gets a fourth tab, Debug.
   - The running version at the bottom ("Version 2026.11.4 · 1fdaf34", "Utvecklingsversion" for local builds); the only place it shows.
8. **Debug** (`/debug`; the tab only shows while the person has Debugging on)
   - The latest 100 steps on the board connection, newest first, in memory only: time, → / ←, the step in Swedish ("Slå på relä 3", "Status: relä 1 och 3 på · ingången av", "Anslutningen bröts: …") and the packet's bytes. The button "Rensa".

Blazor Server gives live updates for free. When `Vm201Client` reports a new status, every open page must update at once, through a singleton service with an event that components subscribe to and unsubscribe from in `Dispose`.

### Design tokens

```css
--bg: #F5F2EC;        /* background */
--card: #FFFFFF;
--ink: #1D1B18;       /* text */
--muted: #5C574F;     /* secondary text, ≥4.5:1 against --bg */
--line: #E3DED5;      /* card border */
--line-strong: #CFC8BC; /* input border */
--chip: #E8E3DA;      /* AV button, badges */
--accent: #C2410C;    /* PÅ, links, primary action */
--accent-tint: #FFF4EC; /* card when the relay is on */
--accent-border: #F0BE9C;
--ok: #2F7D4F;
--danger-bg: #FDECEA; --danger-border: #E5A29B; --danger-ink: #7A1A10;
font: "IBM Plex Sans" (UI), "IBM Plex Mono" (times, labels)
radii: 10–14 px. Touch targets ≥ 44 px. Body text 16 px, relay names 18 px, heading 28 px.
```

**Dark mode** follows the phone's setting (`prefers-color-scheme: dark`); the app has no switch. `app.css` redefines the tokens (warm near-black `--bg: #16140F`, `--card: #221F1A`, `--ink: #F2EEE7`, `--muted: #B3AA9E`, `--accent: #F06A2C` …), plus a few named for it: `--on-ink` (text on an `--ink` background), `--on-accent`, `--danger-solid` (the "Ta bort" button), `--row-line`, `--line-dashed`, `--inactive-bg`/`--inactive-border` and `--backdrop`. Pages take every colour from a token, never a hex value of their own (a test checks), so a new page is dark too.

Use real `<button>` and `<label>` elements, and `aria-pressed` on the PÅ/AV buttons. No emojis and no gradients.

---

## Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": { "Default": "Data Source=/var/lib/contactor/contactor.db" },
  "Vm201": {
    "Host": "",                     // the board's address, e.g. 192.168.1.50; install.sh asks for it
    "Port": 9760,
    "Username": "",
    "Password": "",
    "DisableBoardTimers": true,
    "KeepAliveSeconds": 15,
    "LogPackets": false
  },
  "CloudflareAccess": {
    "TeamDomain": "",               // e.g. myteam.cloudflareaccess.com; install.sh asks for it
    "Audience": "<AUD tag of the Access application>",
    "DevUserEmail": null
  },
  "TimeZone": "Europe/Stockholm"
}
```

No `Urls` in the settings: the .NET image listens on 8080 (`ASPNETCORE_HTTP_PORTS`), and in development Aspire picks the port. ASP.NET Core's data protection keys are kept in `keys/` next to the database, so they survive upgrades.

Anything specific to the installation or secret (the Access team domain and AUD tag, the board's address and password, the tunnel token) is never in the repository or the image. An install script asks for them once, saves them in `/opt/contactor/.env` (readable by root only), and compose passes them in as environment variables, e.g. `CloudflareAccess__Audience`.

---

## Operations

See `DEPLOY.md` for the steps.

- **Versions:** `YYYY.M.D` in UTC without leading zeros, plus `.N` for a later release the same day (`scripts/release-version.sh`). A release is built with `-p:Version=<version>`; every other build is `0.0.0-dev` (`Directory.Build.props`). `AppBuild` reads the version and commit; they show in `/healthz`, the start log, the image label `org.opencontainers.image.version`, the upgrade's output and at the bottom of Inställningar.
- **Publish:** the Images workflow (started by hand) or `scripts/images-publish.sh` builds the solution and runs every test (a failure publishes nothing), builds the image with `dotnet publish -t:PublishContainer`, tags it `YYYY.M.D[.N]`, `latest` and `sha-<commit>`, signs it with cosign by digest (`scripts/images-sign.sh`, keys in `deploy/cosign/`) and tags the commit `v<version>`.
- **Run:** Docker Compose on the Raspberry Pi (Raspberry Pi OS Lite 64-bit on an SD card, an endurance card recommended), from `/opt/contactor` with `compose.yml` and `.env`. The app's container runs as the image's user 1654; `/var/lib/contactor` (database, `backups/`, `keys/`) is a bind mount owned by it. `cloudflared` is pinned by digest and runs a remotely managed tunnel: `<your hostname> → http://contactor:8080`.
- **Upgrade:** `contactor-upgrade` (`deploy/upgrade.sh`) only runs an image signed with one of the keys in its `COSIGN_KEYS`; a systemd timer runs it nightly between 03:00 and 04:00 (`deploy/autoupdate-install.sh`). After a healthy start it keeps the 3 newest versions of the app's image (`KEEP_VERSIONS`, never the running one) and removes older ones, so a 16 GB SD card doesn't fill up.
- **Migration:** run `Database.Migrate()` at start.
- **Backup:** every night at 02:30 the app copies the database with SQLite's `VACUUM INTO` into `backups/contactor-YYYY-MM-DD.db` and keeps the latest 14 (`Backup:Folder`, `Backup:Keep`, `Backup:At`).
- **Health check:** `/healthz` (localhost only, otherwise 404) returns 200 and the VM201 connection status. The image has no shell or curl; `dotnet /app/Contactor.Web.dll healthcheck` asks it and exits 0 or 1, and compose uses that.

---

## Testing

- **Codec:** encode/decode, checksum, the verified example (`02 06 4F 01 A8 03`), split and merged TCP packets, and an invalid checksum.
- **Client against the simulator:** login with and without auth, wrong password, pushed status, reconnect after a disconnect.
- **Scheduling:** with an injected `TimeProvider`. Test weekdays, over midnight, both daylight saving changes, overlaps and the reconciliation at start.
- **Auto-off:** that it triggers, and not for relays turned on by a schedule.
- **Identity:** valid and invalid JWT, wrong `aud`, and that the dev fallback is blocked in Production.
- **CI:** the Build workflow runs `dotnet build` and `dotnet test` on every pull request to `main`, on every merge into `main` and by hand, and shows the results as a "Tests" check (`dorny/test-reporter`). A new push to a pull request cancels its older run. Run the related tests locally before pushing.

## Build order

1. `Contactor.Vm201`: codec, simulator and client, with tests. **Test against the real VM201 before going further.**
2. Web shell: EF model and migration, `CurrentUser` middleware, the Reläer screen with live status and ON/OFF.
3. Event log and the Händelser screen.
4. The Relä screen: name, active, auto-off.
5. Schedules and the scheduler.
6. PWA manifest, operations files and a README with installation steps.

## Conventions

- Nullable on, `TreatWarningsAsErrors` in Release.
- Time: inject `TimeProvider`. Never use `DateTime.Now` directly.
- Log with `ILogger` and structured fields. Never log the VM201 password.
- UI text in Swedish; everything else in English (see Languages).
