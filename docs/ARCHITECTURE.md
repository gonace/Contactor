# Contactor: how the app is built and what it does

The architecture and design of Contactor: what the app does and how it is built. How to work in this repository is in `AGENTS.md`.

---

## Architecture

```
Phone ──▶ the way in the setup guide chose
            ├─ Only on this network: the address of the machine the app runs on
            ├─ Cloudflare: <your hostname> ──HTTPS──▶ Cloudflare Access ──▶ Cloudflare Tunnel
            │               ──▶ cloudflared, started by the app in its container ──▶ http://contactor:8080
            └─ Tailscale: later
                                                         │
                                              Contactor.Web (Blazor Server, .NET 10)
                                              the chosen sign-in checks every request
                                                 │                    │
                                           SQLite (WAL)        TCP :9760 ──▶ VM201
```

- **Never on the internet directly.** No port is opened to the internet. People reach the app through the way in chosen in the setup guide (`Installation.AccessType`), and every request goes through the sign-in chosen there (`Installation.SignInType`, see [Identity](#identity-sign-in)).
  - "Only on this network" (`local`, `LocalNetwork`): runs nothing. People open the address of the machine the app runs on, so the app is as private as that network. Its only sign-in is "No sign-in".
  - "Cloudflare" (`cloudflare`, `CloudflareTunnel`): Cloudflare Tunnel on the installation's own hostname. Its only sign-in is "Cloudflare Access".
  - Tailscale comes later. A new way in is a class of its own and one line in the catalogue.
- **Ways in** (`Features/Access`): `IAccessMethod` runs one, e.g. keeps a tunnel open. `AccessCatalogue` lists each `AccessMethod`: its id, name, description for the guide, and the sign-ins it works with. `AccessChoice` reads the saved one at start and saves the guide's choice (`ChooseAsync`), which is used at once. `AccessHostedService` keeps the chosen one running; a new choice stops the old one and starts the new one. Nothing runs before one is chosen.
- **Sign-ins** (`Features/Identity`): `ISignInMethod` tells who made a request. `SignInCatalogue` lists each `SignInMethod`, and `SignInMethod.Create` gets the `ISignInMethod` registered (keyed) under its id. `SignInChoice` reads the saved one at start and saves the guide's choice (`ChooseAsync`). `SignInMiddleware` checks every request.
- **Cloudflare** (`Features/Cloudflare`): what only Cloudflare has. The tunnel (`CloudflareTunnel`, `CloudflaredProcess`), Access (`CloudflareAccess`, `CloudflareAccessTokenValidator`, `CloudflareAccessKeys`) and their settings (`CloudflareSettings`, `CloudflareMethods` for their ids).
- A saved way in or sign-in that the catalogue doesn't have stops the app at start.
- The app runs in a container and listens on port 8080 on all of the container's addresses. With Cloudflare as the way in, it starts `cloudflared` itself with the tunnel's token, and starts it again with a growing wait (1 s doubling to 1 min) whenever it stops; the image has no shell or process manager. Without a token it waits for one, and a new token starts it again at once. The token comes from the setup guide (`CloudflareSetting`), else from `Tunnel:Token`. It goes to `cloudflared` only through its environment and is never logged. With Cloudflare the port is **never published** on the machine the app runs on, so nobody on the LAN can get around Access.
- **Cloudflare Access** (one-time PIN by e-mail, session length 1 month, an allow list of e-mail addresses) is set up in Cloudflare. Each installation has its own Access team domain (`<team>.cloudflareaccess.com`) and application; the AUD tag is in Zero Trust → Access controls → Applications → Edit → Additional settings.
- Give the Pi and the VM201 DHCP reservations in the router, so their addresses don't change.

---

## Boards (`Contactor.Boards`)

The app talks to a relay board only through `IRelayBoard`, `IBoardConnection` and `IBoardTraffic`, and knows its model from the board catalogue, so another make of board needs its own project with those adapters, a reference from `Contactor.Web` and a line in the catalogue, and nothing else changes. One board per installation for now; several at once is issue #4.

- `IRelayBoard`: `Capabilities`, `State`, `StateChanged`, `TurnOnAsync`/`TurnOffAsync(ChannelMask)` and `RefreshAsync`. Commands go at once or throw `BoardNotConnectedException`; they are never queued. A command isn't confirmed by its return: the next `StateChanged` is the confirmation.
- `BoardState`: `IsConnected`, the relays and digital inputs that are on, and `LastContactUtc`. The board is the source of truth; the app never stores relay state.
- `BoardCapabilities`: how many relays and inputs (and analog inputs, which the app doesn't show yet), and whether the board reports changes by itself.
- `BoardPoller<TReading>`: the loop for a board that doesn't report changes by itself (the VM204, the VM208, the Waveshare and the Devantech boards). The board's own code implements `IPolledBoard<TReading>` (connect, e.g. log in; read the state once); the poller asks every `PollInterval` (2 s) with `TimeProvider`, refreshes `State` (and so `LastContactUtc`) on every poll but reports a state only when it changed (a comparer decides what counts), counts the board as lost after `FailuresBeforeLost` (3) failed polls in a row (a failed command counts as one, so a dead or broken board is lost even while commands keep coming), and connects again with backoff 1 s → 30 s. Commands go through `RunCommandAsync`: a poll under way is cancelled (that doesn't count as a failure) so the command goes at once, it throws `BoardNotConnectedException` once the board is lost (decided under the same lock as the polls), and what the command returns, the board's state right after it, is taken as a poll's answer. A command fails as a poll would: whatever it throws (unreadable or no answer) counts once, except a cancellation by the caller, `BoardNotConnectedException`, with which the command says it sent nothing (the VM204 logging in again), and `BoardRefusedException`, with which it says the board answered but refused every relay in it (a Waveshare exception answer or a Devantech `01`). A refusal starts the count over, as an answered poll does, so quick commands the board refuses never lose it. A failed command is followed by a read at once, so the screens show what the board really did; that read is an ordinary poll that counts on its own, so a dead board is lost after 3 unanswered requests in a row. Outside `RunCommandAsync` the boards still throw `BoardNotConnectedException` ("didn't take the command") for a refusal. `PollNow` asks at once, `Restart` ends the connection or wait (new address or login), and `Forget` (a new address) resets the state to "never answered" and drops an answer from before still on its way.
- `ChannelMask`: a set of channels (relays or inputs) numbered from 1, at most 64; bit 0 is channel 1.
- `IBoardConnection`: where the board is (`BoardEndpoint`) and its login (`BoardLogin`), with the defaults from the board's own settings, changeable while the app runs; `CheckLogin` says what the board can't take, and `RunAsync` keeps the connection open.
- `IBoardTraffic`: each step on the connection as a `BoardTraceEntry` in plain words (direction, text, the bytes, error, routine), for the Debug screen and the toasts. The board writes the words and never puts a password in one.
- **The board catalogue** (`Features/Boards/BoardCatalogue`): every model the app supports, as a `BoardModel` (id, brand, name without the brand, capabilities, default port, login length, an optional login hint in plain words, and whether the login is a password alone, `PasswordOnly`) and how to build it while the app runs (`ActiveBoard`: its adapters, what only it has, and its own small set of services).
- **No default board, and no restart.** The rest of the app talks to one `BoardSwitch`, which is the `IRelayBoard`, `IBoardConnection` and `IBoardTraffic` and passes everything to the chosen board. Before one is chosen it has no relays and never answers: commands throw `BoardNotConnectedException` and nothing connects. `BoardChoice` chooses: at start the saved model (`Installation.BoardType`), and in the setup guide the one picked there, while the app runs. A switch adds the rows for the new board's relays and inputs, stops the old board's connection, starts the new one's, and reports the new board's state as unknown until it answers, as for a new address. An installation in use from before the guide chose the model (the guide done, none saved) keeps the board it ran: `Board:Type`, or the VM201, saved once at start. The app refuses to start on a saved model the catalogue doesn't have. A model is never switched once the guide is done: replacing the board is issue #32. `AddBoards` registers the switch and what every board has in the app: its address and login on the Settings screen (`BoardAddressHandler`), the Debug trace (`BoardTrace`), the Debugging setting and `BoardHostedService`, which keeps the chosen board's connection open.
- **Each board is described in `Contactor.Boards.<Model>/README.md`:** its protocol or routes, its client and simulator, and what to check on a real board. Read it before you change that board.
- **Adding a model:** a project of its own (`Contactor.Boards.<Model>`) with its `IRelayBoard`, `IBoardConnection` and `IBoardTraffic`, a `<Model>Boards.Create` in `Features/<Model>` that builds it with what only it has, one line in the catalogue, and a `README.md` that describes the board. Nothing else in the app changes.
- `ISimulatedBoard`: a fake board of a model for development and tests, on a local port: its relays, inputs and connected clients, a relay switched or an input set "at the board", dropped connections, a change event, and the traffic as `BoardTraceEntry` from the board's side (`Sent` is still from the app to the board). A model with timer programs of its own also implements `ISimulatedBoardTimers`. Each model's project brings its own (`Vm201Simulator`, `Vm204Simulator`, `Vm208Simulator`, `WaveshareSimulator`, `DevantechSimulator`).
- What only one board has stays in its own project and its `Features/<Board>` folder, and runs only while that board is the chosen one (`ActiveBoard.Extras`, started before its connection and stopped with it): today the VM201's own timers (`Vm201TimersService`) and the Waveshare (B) and (C) relay modes (`Features/Waveshare/WaveshareRelayModesService`).
- The database holds one relay per relay on the board (`IRelayBoard.Capabilities.RelayCount`, at most 64) and one input per digital input (`InputCount`), seeded at start and when a board is chosen. Rows above the count, left from a board that had more, are kept but not shown, offered or switched.

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
  MaxOnMinutes    int?           auto-off after a manual on, or after it came on outside the app; null = never. Default 120.
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
  Actor           text           e-mail, or "schedule" | "auto-off" | "external" | "system" | "input:n" (an input's actions)
  Action          text           "on" | "off" | "rename" | "activate" | "deactivate" |
                                 "schedule-add" | "schedule-edit" | "schedule-delete" |
                                 "connection-lost" | "connection-restored" | "input-changed" |
                                 "board-timers-disabled" | "board-relay-modes-reset" | "board-relay-modes-not-reset" |
                                 "board-address-changed" | "board-login-changed"
  GroupId         int?           the group it concerns; not a foreign key, events outlive a deleted group
                                 Group actions: "group-on" | "group-off" | "group-start-failed" | "run-on-start" |
                                 "run-on-end" | "interlock-trip" | "group-create" | "group-edit" | "group-delete"
  InputId         int?           the input an "input-changed" concerns; not a foreign key
  Details         text?          JSON, e.g. {"from":"Relay 4","to":"Ventilation – Paint shop"}

RelayRuntime            (internal, for auto-off; one row per relay, seeded at start)
  RelayId         int PK
  OnSinceUtc      datetime?
  AutoOffAtUtc    datetime?
  TurnedOnBy      text?          e-mail, "input:n" (counts as a person), "schedule", or "external" (on outside the app,
                                 with the relay's own auto-off; a running schedule window takes it over)
  Version         int            raised at every save; a save from a row changed since it was read fails
                                 Written only through RelayRuns (Features/Relays): after an ON the one who turned it on
                                 owns the run; after an OFF only the run that was on when it went out ends; without a
                                 command a change applies only to the run as it was read or as its rule requires. On a
                                 conflict RelayRuns reads the row again, applies the same wish by its rule, and saves again.

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
  BoardType       text?          the model chosen in the setup guide (catalogue id); null: no board yet
  AccessType      text?          the way in chosen in the setup guide: "local" | "cloudflare"; null: not chosen yet
  SignInType      text?          the sign-in chosen in the setup guide: "none" | "cloudflare-access";
                                 null: not chosen yet, and the app is in setup mode (see Identity)
                                 The migration FillInstallationMethods gave installations already in use "cloudflare"
                                 and "cloudflare-access", which they all had.
  SetupDoneUtc    datetime?      when the setup guide was finished; null: every page opens on the guide
                                 The migration that added it marked installations already in use as done.

CloudflareSetting       (one row; Cloudflare's settings saved in the setup guide)
  Id              int PK = 1
  TunnelToken     text?          the tunnel's token; a secret: never logged or shown, given to cloudflared only
                                 through its environment. In the backups, like the board's password.
  Address         text?          the tunnel's public hostname, where people open the app, e.g. relays.example.com
  TeamDomain      text?          the Access team domain: <team>.cloudflareaccess.com
  Audience        text?          the Access application's AUD tag
                                 A column without a value takes Tunnel:Token, CloudflareAccess:TeamDomain or
                                 CloudflareAccess:Audience from the settings (install.sh).

BoardWatchState         (one row; the app's contact with the board over time, kept by BoardEventRecorder)
  Id              int PK = 1
  LastContactUtc  datetime?      saved every 5 minutes while there is contact (spares the SD card)
  LongGapEndedUtc datetime?      when the app last came back after more than 10 min without contact (a crash,
                                 a board that was off). A reboot or an upgrade is a short gap: the inputs' times
                                 stay. At start the inputs are compared with their last logged state; one that
                                 changed meanwhile, or changed during a lost contact, is logged with
                                 {"noticed":true} and its time is unknown.

Input                   (one per digital input on the board, seeded at start like relays, never deleted)
  Id              int PK         1 to the board's input count (at most 64)
  Name            text           e.g. "Door" (max 40); "Input n" to begin with. Its state is always "Closed"
                                 (the board reads it as on) or "Open": an input is an electrical contact
  IsActive        bool           false = hidden from the list by default
  NotifyOnOpen    bool           for later: notifications (not shown yet)
  NotifyOnClose   bool

InputAction             (a relay or a group an input switches: exactly one of RelayId and GroupId is set)
  Id              int PK
  InputId         int FK
  RelayId         int? FK        a relay in no group; UNIQUE with InputId
  GroupId         int? FK        cascade delete with the group; UNIQUE with InputId
  WhenClosed      bool?          true = turn on, false = turn off, null = nothing
  WhenOpened      bool?
```

- `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout=5000` at start.
- **SQLite stays the database.** Keep SQLite-specific code to `Persistence` (the start-up settings above) and `Features/Backup` (`VACUUM INTO`); everything else goes through EF Core, so a later switch to PostgreSQL stays a small change.
- **Relay state is not stored.** The VM201 is the source of truth; the app always shows the board's latest `CMD_STATUS`.
- Every timestamp is stored in UTC and shown in Europe/Stockholm. Schedules are stored as local time, see below.
- EF Core migrations: `dotnet tool run dotnet-ef migrations add <Name> --project Contactor.Web --context ContactorDbContext --output-dir Persistence/Migrations --namespace Contactor.Web.Persistence.Migrations`. The tool puts the model snapshot in a wrong folder named after the namespace; move it to `Persistence/Migrations/`. The database file is `/data/contactor.db` in the container (`/var/lib/contactor` on the Raspberry Pi's SD card). The app writes little (a row a minute from the scheduler, the time of contact every 5 minutes, the event log, a nightly backup), so a good SD card is enough; an endurance card is recommended.

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
- **Every relay that is on gets an auto-off.** A relay on without an owner (switched at the board's own button or web page, during an outage, or after a press the board didn't confirm) gets the relay's own "Turn off automatically after", counted from when the app saw it on (`BoardEventRecorder`: on an external ON, and at start for a relay that is on with nobody's run; `AutoOffService`: at every check, for one on with nobody's run that the app didn't see come on, e.g. after an OFF the board was sent but didn't carry out). `TurnedOnBy` is `external`, and its status line reads "On since 14:02 · outside the app · off 16:02". A group on without an owner gets the group's own auto-off the same way (`GroupPlanner.GiveOwnerlessAutoOff`). During a running window of its schedule, the scheduler takes such a relay or group over (`schedule`, no auto-off), so it goes off at the OffTime. Nobody guesses who switched it.
- **A relay that is off keeps no run.** `AutoOffService` ends, at every check, the run of a relay the board shows off (e.g. one that went off while the app was down, or whose "off outside the app" gave way to another writer), so its status line doesn't keep saying "On since". Not while an ON for it is still on its way (`ExpectedChanges.IsPending`), and only the run as read (`RelayRuns.EndOutside`); nothing is logged.

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
- A relay in a group is only switched through its group (`RelayControl` refuses), has no auto-off or schedule of its own, and can't get a schedule while in it (its group can). A relay with its own schedules, or one an input controls, can't join a group. Which relays and their order, creating and deleting only change while the whole group is off.
- **Schedules:** a group has schedules of its own, run by the scheduler by the same rules as a relay's (catch-up, ON wins on overlap, a missed OFF only stops what the schedule started, a person's start and auto-off win). OnTime starts the group in its order through `GroupSequencer`, with Actor `schedule` and no auto-off; OffTime stops it with its run-on times. The sequencer logs both as `group-on`/`group-off`. A group that is already partly on at OnTime (e.g. only the fan, switched by hand) is left as the person set it, with their auto-off; the OffTime still stops it.
- **Auto-off** (`RelayGroup.MaxOnMinutes`) stops the whole group with its run-on times. **"All off"** stops each group with its run-on times and turns the other relays off at once.
- **No board:** nothing is sent or queued; run-on times keep counting, and on reconnect the interlock goes first, then what fell due. A command the board hadn't confirmed gets a fresh 5 s to show; the relay a stop needs for its run-on (e.g. the fan after a trip) is sent once more, then given up (`group-start-failed`) if it still doesn't come on.
- **Safety:** the app's interlock only works while the Pi and the app run. Where safety depends on it (heat without the fan), it must also be wired in hardware, e.g. by feeding the heater relay through the fan relay's output. That is the electrician's decision; the app is an extra layer.

### Inputs that switch relays and groups
- An input can control any number of relays and groups (`InputAction`, `InputActionsHandler`), each with its own action when it closes and when it opens: turn on, turn off or nothing. A relay in a group only through its group.
- `InputActionService` acts like a press of the button at that moment, with the input as the actor (`input:n`): a relay through `RelayControl` (with its auto-off), a group through `GroupSequencer` (in its order, with its run-on and auto-off). A relay already in that state is left alone (so one its schedule turned on stays the schedule's), unless the opposite command is still on its way (`ExpectedChanges.IsPending`); a group decides itself what there is to stop, and one fully on and idle isn't started again.
- Only a change the app saw as it happened acts: `BoardEventRecorder` hands it on (`InputChangeFeed`) once it is logged. A change noticed after a gap, or at start, does nothing. Without the board nothing is sent or queued.
- Events name the input as who did it: "Door turned off the group Fan heater" (a dashed badge with "I").

### The board's own timers
- The board's built-in timer programs compete with the app's schedules. On the first successful connection, and at the first answer after a new address (it may be another VM201): send `CMD_TMR_DIS 0xFF` and log it with Actor `system`. Configurable through `Vm201:DisableBoardTimers=true`.

### External changes
- If `CMD_STATUS` shows a change the app didn't ask for (through the board's web GUI, the physical button or timers): log it with Actor `external`.
  - Implementation: keep a short list of "expected changes" that is cleared after 5 s.
  - A command that fails (refused, unreadable or no answer) logs nothing, and drops the expectation it added (`ExpectedChanges.Forget`), so a relay the board switched anyway shows on the next status as an external change instead of being taken for the app's own and lost. That holds for single relays, "All off", groups, auto-off and schedules alike.
  - A relay that comes on this way gets its own auto-off (see Auto-off), whoever turned it on. The setup guide's "Try" turns a relay off 2 s after a failed ON if the board shows it on, as the try promised only a moment.
- **A press the board didn't confirm** (no answer, an unreadable one, no connection): the relay's card (Relays) or status card (Relay) shows "The board didn't answer. The relay may or may not have switched." with **Try again** (the same command, sent at once, only while the board is online) and **Leave it**. It goes away when the person chooses or the board shows the relay as they wanted it. Nothing is guessed or queued.

### Connection
- A new installation has no board until the setup guide chooses one, and then may have no address for it yet (e.g. `Vm201:Host` empty): nothing connects, and no contact is lost, until one is saved (in the setup guide or on Settings). `BoardHostedService` waits for it.
- When contact is lost for more than 30 s, `connection-lost` is logged, and `connection-restored` when it comes back.
- The UI shows a red warning: "No contact with the relay board. Since HH:mm. The states below may be wrong."
- **Before the board's first answer** since the app started (`BoardState.HasAnswered`) nothing is known: the warning reads "No contact with the relay board yet. Waiting for its first answer.", the heading leaves out "n of m on", and every relay, group and input card, and the status card on the Relay, Group and Input screens, is grey all over, with "Waiting for the board" and a dashed "–" in place of the ON/OFF button or the Open/Closed badge. Once it has answered, a lost contact keeps showing the last known states with the warning above. A new address (Settings or the setup guide) forgets the old board's state (`Vm201Client.SetEndpoint`, `BoardPoller.Forget`): it is unknown again until the board there answers, an answer from the old address still on its way is dropped, and `BoardEventRecorder` takes the new board's first status as at start, comparing no relays with the old board's. A new login keeps the states: it is the same board. The relays' and groups' runs (who turned them on, their auto-off, a schedule's catch-up) are kept: the app can't tell a new board from the same board at a new IP, and for the same board clearing them would leave a fan heater without its auto-off. For a new board this errs towards switching off.

---

## Identity (sign-in)

- **Every request goes through the chosen sign-in.** `SignInMiddleware` runs before anything that serves content, including static files and Blazor's SignalR connection. It asks the chosen sign-in method (`SignInChoice.Current`) who made the request. Nobody: answer 403 "Access denied.". An e-mail missing from `User` gets a new `User`, with DisplayName = the part before @, with a capital first letter.
- `/healthz` and `/setup/reload` need no sign-in and answer localhost only; in Development `/health` and `/alive` need none either.
- **The sign-ins** (`SignInCatalogue`):
  - "Cloudflare Access" (`cloudflare-access`, `CloudflareAccess`): see below.
  - "No sign-in" (`none`, `NoSignIn`), "Not recommended" in the guide: every request is let in as one shared person, `someone@contactor.invalid`, whose name is "Someone". The events can't tell people apart.
- **The sign-in must fit the way in.** Each way in lists the sign-ins it works with: "Only on this network" only No sign-in, Cloudflare only Cloudflare Access. So No sign-in is never used with a public link. The guide offers only what fits, and at start the app refuses a saved sign-in that doesn't fit its way in, or has no way in (`CheckSignInFitsWayIn`); the message says to run `reset-sign-in`.
- **Setup mode:** while no sign-in is chosen (`Installation.SignInType` empty), nobody is signed in, and `SetupMode` lets through only the static files and the setup code's page (`/setup/code`). A browser with the setup code's cookie also gets the guide (`/setup`) and Blazor's connection (`/_blazor`). Any other GET goes to the code page (or the guide once the code is typed); any other request gets 403. An empty database always starts this way, also in development.
- **The setup code** (`SetupCode`): 8 letters and digits without look-alikes, shown as `ABCD-EFGH`. A new code is made at each start without a sign-in, and when `reset-sign-in` clears the sign-in. It is kept in memory only and written to the log. Case, spaces and dashes don't matter. The right code gives the browser the cookie `contactor-setup` (HttpOnly, SameSite Lax, Secure over HTTPS), signed with data protection and tied to the current code, so a new code ends it. After 5 wrong codes in a row, no code is tried for a minute; a new code doesn't reset that count.
- **Locked out:** `docker exec contactor dotnet Contactor.Web.dll reset-sign-in` (`SignInReset`) clears `Installation.SignInType` and calls `POST /setup/reload`. That reads the sign-in again and makes a new setup code only when the sign-in has just gone; it returns nothing. The guide then opens again at "How do people sign in?", or at "How do people reach the app?" for a Cloudflare installation without a saved address (one set up with install.sh), which asks for the tunnel's address first; an empty token keeps the saved one. The way in (so the tunnel keeps running), the board, the relays, the events and the schedules stay. When the app isn't running, it writes a code when it starts.
- **Cloudflare Access:** validate `Cf-Access-Jwt-Assertion`: RS256, `aud` against the Access application's AUD tag, `iss` against `https://<TeamDomain>`, lifetime against `TimeProvider`. Missing or invalid JWT: denied.
  - **The settings** come from `CloudflareSettings`: what the setup guide saved (`CloudflareSetting`), else `CloudflareAccess:TeamDomain` (the host name only) and `CloudflareAccess:Audience` from the configuration. They are read at each request, so a change takes effect at once. The team domain must end in `.cloudflareaccess.com`, with one label before it: the app fetches its keys from there, and whoever controls that domain decides who may sign in. The app doesn't start while Cloudflare Access is the sign-in and its settings can't be used.
  - **The e-mail comes from the JWT's `email` claim**, not from the `Cf-Access-Authenticated-User-Email` header: the claim is signed, the header isn't.
  - The JWKS is fetched from `https://<TeamDomain>/cdn-cgi/access/certs` and cached for 1 h. A JWT with an unknown key refetches the list at once, at most once a minute. A failed fetch keeps the keys it had; without keys everyone is denied. A new team domain drops the old team's keys.
- Blazor Server: the middleware sets `HttpContext.User`, which Blazor carries into the circuit when it starts. Components read the user through the scoped `CurrentUser` (built on `AuthenticationStateProvider`), since headers are gone in the SignalR circuit afterwards. It is null only for a setup connection, let in by the setup code.

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
   - The link "New group" under the cards, then one button for inactive relays and inputs: "Show 2 inactive relays", "Show 1 inactive input", or "Show 3 inactive" for both, and "Hide inactive …". Inactive relays and inputs have a dashed border.
   - "Inputs", after the relays: one read-only card per active input on the board (`InputsHandler`); its text links to its screen: "INPUT 1", its name, "Open since 14:02" (since its latest `input-changed`), or else "Open since unknown" with an ⓘ right after it (`WhyUnknown.razor`), whose reason shows as a tooltip on hover, on keyboard focus, or when pressed (a second press or a tap elsewhere closes it). The board only reports what an input reads now, so the time is unknown for a change the app only noticed after a gap in contact, or when a long gap came after it (see `BoardWatchState`). Beside it, a badge with its state, "Open" or "Closed" (green when closed). No button: the app can't switch an input.
   - The list has a section each for "Groups", "Relays" and "Inputs", in that order, each with a small heading and a grid of its own, so the cards line up and Tab goes row by row. With only relays (no groups and no input shown) there are no headings. "Relays" has no heading and no section when there is no relay card to show (every relay in a group, or the rest hidden as inactive).
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
   - "Relays in start order": each relay with its number, role name, state ("Needs Fan to be on", "Starts in 3 s", "Keeps running for 4 min 59 s"), its own ON/OFF within the interlock, up/down and "Remove from the group"; between two relays "On start, wait" (seconds) and "On stop, run on". Adding a relay offers only relays in no group, without schedules and controlled by no input. Order and members only change while the group is off.
   - "Turn off automatically after", "Schedules" (as on the Relay screen, "Add" goes to `/group/{id}/schedule/new`), the group's latest 4 events, the safety note about wiring the interlock in hardware, "Delete the group" with a confirmation, and "Save".
   - Icons are Font Awesome Free solid icons (CC BY 4.0), inlined as SVG in `GroupIcon.razor`; no icon font or script.
5. **New group** (`/group/new`): a guide in 4 steps, "STEP n OF 4" with a progress bar, "Cancel" on the first step and "Back" after.
   1. "Name and icon": the name and the 8 icons (Fan, Heat, Light, Air, Paint, Pump, Tools, Power).
   2. "Which relays?": all the board's relays; the order they are chosen in (#1, #2 …) is the start order. A relay in another group ("Already in X"), with schedules of its own ("Has its own schedules") or controlled by an input ("Controlled by Door") can't be chosen. At least 2.
   3. "Order and rules": up/down, "Name in the group" for each relay (its name to begin with) and the waits between the steps.
   4. "Review and create": the card as it will look, and in plain words when it starts, when it stops, the interlock and the auto-off (2 h, changed on the group's page). "Create group" only works while every chosen relay is off.
6. **Events** (`/events`)
   - Filter: All / People / Automatic.
   - Grouped by day (TODAY, YESTERDAY, date).
   - Row: time in mono, a round badge with an initial, and text.
     - People: a filled badge with their initial.
     - Schedule/auto/system: a dashed badge with "A".
     - An input's change names it: "Door: Open" or "Door: Closed" (a dashed badge with "I"). Every input is logged, each with its `InputId`.
     - A change only noticed after a gap in contact reads "Door: Open · time unknown" with an ⓘ giving the reason (`EventWhat.razor`, `WhyUnknown.razor`), here and in the latest events on the Relay, Group and Input screens.
     - Group events name the group: "John turned on the group Fan heater · Fan → 3 s → Heat", "Run-on: Fan keeps running for 5 min". A safety shut-off ("Heat turned off because Fan was off"), a start that failed and a Waveshare board whose relays couldn't be set to normal mode show as a red row.
   - 50 at a time, with the button "Show more" for the next 50 (a button rather than loading on scroll, so no JavaScript is needed).
7. **Settings** (`/settings`)
   - "Relay board": the model ("Velleman VM201 · 8 relays"), the board's address ("Address") and "Port" (the model's default port as its placeholder), its login, for a model that has one ("The board has no login", or "User name" and "Password", with the model's login hint, e.g. "At most 8 characters: letters a–z, digits and plain symbols.", or else "At most N characters."; a model whose login is a password alone, the Devantech boards, shows only "Password" and "The board has no password", with its hint alone; the password is never shown, an empty field keeps it), the live status ("Board online at 192.168.1.50:9760", "No contact with the relay board at …" or "The relay board has no address yet"), and "Save". Saving moves the connection at once and is logged ("John changed the relay board's address from … to …", "John changed the relay board's login", never the values).
   - "People": everyone who has signed in, by e-mail, with their name ("Alex.smith" → "Alex") and "Save".
   - "Backups": the newest nightly copy ("Latest: 02:30 · 68 kB"), "Make a copy now" and "Download" (`GET /settings/backup`, behind the sign-in): the newest copy as a zip (`contactor-YYYY-MM-DD.zip` with the `.db` inside), so browsers don't warn about a `.db` file. The copies on the Pi stay plain `.db` files.
   - "Debugging": the checkbox "Show the traffic to the relay board", per person and saved at once. While it is on, that person sees each packet to and from the board as a toast at the bottom of every page (at most 3, 6 s each, errors red; not the keep-alive or the channel names), and gets a fourth tab, Debug.
   - The running version at the bottom ("Version 2026.11.4 · 1fdaf34", "Development version" for local builds; `AppBuild.Text`); the only place it shows, except on a desktop, where it sits at the bottom of the side menu instead.
8. **Debug** (`/debug`; the tab only shows while the person has Debugging on)
   - The latest 100 steps on the board connection, newest first, in memory only: time, → / ←, the step in plain words ("Turn on relay 3", "Status: relay 1 and 3 on · input off", "Connection lost: …") and the packet's bytes. The button "Clear".
   - Routine steps (the VM201's keep-alive and channel names, the polled boards' polls and answers that change nothing) are only counted (`BoardTrace.RoutineLeftOut`): "N routine steps left out, such as status checks that changed nothing", kept current at most once a second (`RoutineCounted`), so a board that is asked every 2 s can't push the rest out of the list.

9. **Setup** (`/setup/code`, `SetupCodePage.razor`; `/setup`, `SetupGuide.razor`; `Features/Setup`): a new installation has no way in, no sign-in and no board. While no sign-in is chosen, every page opens on the setup code's page (see Identity). After that, every page opens on the guide (`SetupGate.razor`) until someone finishes it. Whoever has the setup code goes through Part 1; anyone signed in may go through the rest. No tab bar. The header shows "PART 1 OF 2 · THE APP" or "PART 2 OF 2 · THE BOARD", and "STEP n OF 4" with a progress bar. "Back" goes to the step before, but never to the setup code, and never back to a sign-in that is saved.
   - **Part 1, the app** (`SetupSignInHandler`):
     1. "Welcome to Contactor": "Setup code", and "WHERE IS IT?" (the app's log, e.g. `docker logs contactor`). This page has no live connection (`[ExcludeFromInteractiveRouting]`; `App.razor` renders such pages without it), because only an ordinary page's answer can set the cookie. "That isn't the setup code. Check the log and try again.", or after 5 wrong codes "Too many wrong codes. Wait a minute, then try again.".
     2. "How do people reach the app?": the ways in as choices, each with its description. "Only on this network" is tagged "SIMPLEST" and is chosen to begin with. Cloudflare also asks for the "Tunnel token" and the "Address" (the tunnel's public hostname). Both are saved in `CloudflareSetting`, and the tunnel starts at once (`AccessChoice.ChooseAsync`, `AccessHostedService`).
     3. "How do people sign in?": every sign-in. Those that fit the way in can be chosen, with the recommended one first ("RECOMMENDED"). The rest show greyed out with their reason, e.g. "Only with Cloudflare.". No sign-in is tagged "NOT RECOMMENDED" and warns that anyone on the network can switch the relays. Cloudflare Access also asks for the "Team domain" and the "AUD tag". With Cloudflare as the way in, the step shows whether the tunnel is connected. Saving the sign-in ends setup mode (`SignInChoice.ChooseAsync`). With Cloudflare Access, only the tunnel's address answers from then on. So the page says "Go on at your new address" and links there. With No sign-in the guide loads again and goes on as "Someone".
     4. "About you": "Your name" (saved as the person's DisplayName) and "YOU'LL NEED". Skipped with No sign-in.
   - **Part 2, the board:**
     1. "Which relay board?": "Brand", then "Model", none chosen to begin with. Each field opens a list from the bottom of the screen on a phone, and a dropdown under the field on a tablet or desktop: the brands with how many models each has, then only that brand's models with what they have ("8 relays · 1 input"). A new brand opens its models at once. "Next" works once a model is chosen. The one chosen is saved and the app switches to it at once, while it runs (`BoardChoice`), and the guide goes straight on.
     2. "Where is the board?": address, port (empty: the model's default) and login, saved through `BoardAddressHandler`; "Next" waits up to 10 s for the board (`BoardContact`, which ignores a connection to the old address) and shows "Board online" or "No answer from …". "Go on without a connection" saves what was typed and goes on.
     3. "Name your relays": a name ("Relay n" as its hint) and "In use" for each relay, saved as on the Relay screen. "Try" turns a relay on for 2 s through `RelayControl` (refused for a relay in a group), off again even if the page closes.
     4. "All set, {name}": the board and "6 in use, 2 hidden", links to "Make a group" and a relay's page for a schedule, and "Go to Relays". Reaching it marks the guide done.
   - After `reset-sign-in`, the guide opens behind a new setup code at "How do people sign in?". A Cloudflare installation without a saved address (one set up with install.sh) opens at "How do people reach the app?" instead, which asks for the tunnel's address first; an empty token keeps the saved one. An installation that was done goes on to the relays once a sign-in is chosen.

10. **Input** (`/input/{id}`, `InputDetail.razor`, `Features/Inputs`)
   - "INPUT n · NAME", the checkbox "Active: shown in the list", the state now ("NOW", "Open since 14:02" or "Open since unknown" with the same ⓘ, and its badge), "Groups and relays" (a card per relay or group it controls: "Group: Fan heater" or "Relay 4: Lighting", "CLOSES" and "OPENS" each with Turn on / Turn off / Nothing, and "Remove"; "+ Add a group or relay" offers what it doesn't control yet, groups first), its latest 4 events with "All events", and "Save". Saving isn't logged. Like the Relay screen: the side menu on a desktop, two columns and Save at the top right.

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
  "Board": { "Type": null },        // only for an installation in use from before the setup guide chose the model; vm201 when not set
  "Vm201": {
    "Host": "",                     // the board's address, e.g. 192.168.1.50; install.sh asks for it. Empty (without install.sh): the setup guide asks
    "Port": 9760,
    "Username": "",
    "Password": "",
    "DisableBoardTimers": true,
    "KeepAliveSeconds": 15,
    "LogPackets": false
  },
  "Vm204": {                        // only read while the VM204 is the board
    "Host": "",
    "Port": 80,
    "Username": "",
    "Password": "",
    "PollSeconds": 2
  },
  "Vm208": {                        // only read while the VM208 is the board; its API has no login
    "Host": "",                     // the board's address; empty: the setup guide asks
    "Port": 80,
    "PollSeconds": 2
  },
  "Waveshare": {                    // only read while a Waveshare board is the board; Modbus has no login
    "Host": "",                     // the board's address; empty: the setup guide asks
    "Port": 4196,
    "PollSeconds": 2,
    "ResetRelayModes": true         // (B) and (C): set every relay to normal mode on connecting, so no input switches one
  },
  "Devantech": {                    // only read while a Devantech board is the board
    "Host": "",                     // the board's address; empty: the setup guide asks
    "Port": 17494,
    "Password": "",                 // the board's TCP password, its only login; empty: none
    "PollSeconds": 2
  },
  "CloudflareAccess": {             // optional: only where the setup guide saved none (CloudflareSetting)
    "TeamDomain": "",               // e.g. myteam.cloudflareaccess.com; install.sh asks for it
    "Audience": ""                  // the AUD tag of the Access application
  },
  "Tunnel": { "Token": "" },        // optional: the Cloudflare tunnel's token, where the setup guide saved none
  "TimeZone": "Europe/Stockholm"
}
```

No `Urls` in the settings: the .NET image listens on 8080 (`ASPNETCORE_HTTP_PORTS`), and in development Aspire picks the port. ASP.NET Core's data protection keys are kept in `keys/` next to the database, so they survive upgrades.

Anything specific to the installation or secret (the Access team domain and AUD tag, the board's address and password, the tunnel token) is never in the repository or the image. The setup guide saves them in the database. An install script can also ask for them once, save them in `/opt/contactor/.env` (readable by root only), and compose passes them in as environment variables, e.g. `CloudflareAccess__Audience` and `Tunnel__Token`. What the guide saved wins over them.

---

## Operations

See [`DEPLOY.md`](DEPLOY.md) for the steps.

- **Versions:** `YYYY.M.D` in UTC without leading zeros, plus `.N` for a later release the same day (`scripts/release-version.sh`). A release is built with `-p:Version=<version>`; every other build is `0.0.0-dev` (`Directory.Build.props`). `AppBuild` reads the version and commit; they show in `/healthz`, the start log, the image label `org.opencontainers.image.version`, the upgrade's output and at the bottom of Settings.
- **Publish:** the Images workflow (started by hand) or `scripts/images-publish.sh` builds the solution and runs every test (a failure publishes nothing), builds and pushes the image from the `Dockerfile` with `docker buildx` (`scripts/images-build.sh`), tags it `YYYY.M.D[.N]`, `latest` and `sha-<commit>`, signs it with cosign by digest (`scripts/images-sign.sh`, keys in `deploy/cosign/`) and tags the commit `v<version>`.
- **Run:** Docker Compose on the Raspberry Pi (Raspberry Pi OS Lite 64-bit on an SD card, an endurance card recommended), from `/opt/contactor` with `compose.yml` and `.env`. One container, run as the image's user 1654; the Pi's `/var/lib/contactor` is mounted as `/data` (database, `backups/`, `keys/`) and owned by it. `cloudflared` is pinned by digest in the `Dockerfile` and, with Cloudflare as the way in, runs a remotely managed tunnel: `<your hostname> → http://contactor:8080`. An installation whose `compose.yml` predates `/data` still finds its database in `/var/lib/contactor` and logs that `install.sh` should be run again (`PersistenceServiceCollectionExtensions.Resolve`).
- **Upgrade:** `contactor-upgrade` (`deploy/upgrade.sh`) only runs an image signed with one of the keys in its `COSIGN_KEYS`; a systemd timer runs it nightly between 03:00 and 04:00 (`deploy/autoupdate-install.sh`). After a healthy start it keeps the 3 newest versions of the app's image (`KEEP_VERSIONS`, never the running one) and removes older ones, so a 16 GB SD card doesn't fill up.
- **Migration:** run `Database.Migrate()` at start.
- **Backup:** every night at 02:30 the app copies the database with SQLite's `VACUUM INTO` into `backups/contactor-YYYY-MM-DD.db` and keeps the latest 14 (`Backup:Folder`, `Backup:Keep`, `Backup:At`).
- **Health check:** `/healthz` (localhost only, otherwise 404) returns 200 and the board's connection status (`boardConnected`, `boardLastContactUtc`). The image has no shell or curl; `dotnet /app/Contactor.Web.dll healthcheck` asks it and exits 0 or 1, and compose uses that.
- **Locked out:** `docker exec contactor dotnet Contactor.Web.dll reset-sign-in` forgets the sign-in, and the setup guide opens again behind a new setup code in the log (see [Identity](#identity-sign-in)).

---

## History

How the app was first built, in its first weeks. It is kept as a record, not as a plan.

### Build order

1. `Contactor.Boards.Vm201`: codec, simulator and client, with tests. **Test against the real VM201 before going further.**
2. Web shell: EF model and migration, `CurrentUser` middleware, the Relays screen with live status and ON/OFF.
3. Event log and the Events screen.
4. The Relay screen: name, active, auto-off.
5. Schedules and the scheduler.
6. PWA manifest, operations files and a README with installation steps.
