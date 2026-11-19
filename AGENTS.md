# Contactor – the relay app

Contactor is an open-source mobile web app for controlling the 8 relays on a **Velleman VM201** ethernet relay board from a phone, e.g. in a workshop, where the relays drive fan heaters, a compressor, ventilation, lighting and more. It started as the app for one workshop, run by a licensed electrician and a few friends.

The users are not technical. **Simplicity comes before features**: big buttons, plain words, no menus that aren't needed.

**How the app is built and what it does** is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): the architecture, the boards, the simulator, the data model, the domain rules, identity, the screens, configuration and operations. Read the sections a change touches before you change it. The screens are described there; the running app is the reference for layout, texts, colours and sizes.

## Safety rules every change must keep

The relays drive real machines. Each rule is told in full in its section of `docs/ARCHITECTURE.md`.

- **The board is the source of truth.** The app never stores relay state. ([Boards](docs/ARCHITECTURE.md#boards-contactorboards), [Data model](docs/ARCHITECTURE.md#data-model-sqlite))
- **Nothing is queued.** While the board is disconnected, nothing is sent or queued for later. ([Manual control](docs/ARCHITECTURE.md#manual-control), [Relay groups](docs/ARCHITECTURE.md#relay-groups))
- **A group's interlock is never bypassed.** A relay may only be on while every relay above it is. This is checked first on every status, and at start and reconnect. A relay in a group is only switched through its group. ([Relay groups](docs/ARCHITECTURE.md#relay-groups))
- **The scheduler turns nothing off at start or reconnect,** except auto-off times that have already passed and a schedule's missed OffTime for a relay that schedule turned on. ([Scheduling](docs/ARCHITECTURE.md#scheduling))
- **Secrets stay secret.** The board's password and the access services' tokens and keys are never logged or shown. Nothing specific to an installation (the board's address and password, an access service's settings and tokens) is in the repository or the image. ([Architecture](docs/ARCHITECTURE.md#architecture), [Data model](docs/ARCHITECTURE.md#data-model-sqlite), [Configuration](docs/ARCHITECTURE.md#configuration-appsettingsjson))
- **Never on the internet directly.** The app is reached from outside only through an access service such as Cloudflare or Tailscale, never through a port opened to the internet. ([Architecture](docs/ARCHITECTURE.md#architecture))
- **Every request goes through the chosen sign-in.** "No sign-in" is allowed only with "Only on this network", never with a public link; the app refuses to start on a sign-in that doesn't fit its way in. Until a sign-in is chosen, only the setup code's page and, with its cookie, the setup guide answer. `/healthz` and `/setup/reload` need no sign-in and answer localhost only. ([Identity](docs/ARCHITECTURE.md#identity-sign-in))
- **Every colour comes from a design token,** never a hex value of its own, so dark mode works on every page. ([Design tokens](docs/ARCHITECTURE.md#design-tokens))

---

## Languages

- **Everything is in English:** the UI, code, comments, log messages, tests, commit messages, pull requests, issues, `README.md` and this file. Translations of the UI may come later.

---

## Stack and structure

- .NET 10 (LTS), C#, Blazor Server (interactive server rendering), EF Core + `Microsoft.EntityFrameworkCore.Sqlite`
- No JavaScript framework. No external CSS framework; one small stylesheet of our own, following the design tokens in `docs/ARCHITECTURE.md`.
- Target: one container image (`ghcr.io/gonace/contactor`, linux/arm64 and linux/amd64), built from the `Dockerfile` on Ubuntu chiseled-extra, which includes the time zone data the schedules need, with `cloudflared` and Tailscale (`tailscaled`, `tailscale`) copied in; everything it keeps is in `/data`. Run with Docker Compose on the Raspberry Pi. The public demo has its own image (`ghcr.io/gonace/contactor-demo`) from `Contactor.Demo/Dockerfile`, run with `deploy/demo/compose.yml` (`docs/DEMO.md`).

No `src/` or `tests/` folders: every project sits directly in the repository root. All tests live in **one** test project, `Contactor.Tests`, with a folder per area (`Velleman/`, `Web/` …). Don't create more test projects.

```
Contactor.slnx
Dockerfile                  # The image: the app, cloudflared and Tailscale, for linux/arm64 and linux/amd64
global.json                 # SDK version and test runner (Microsoft.Testing.Platform)
Directory.Build.props       # Shared build settings
Directory.Packages.props    # Central package management: every package version is here
Contactor.Boards/           # What any relay board is to the app: IRelayBoard, IBoardConnection, IBoardTraffic, BoardModel, BoardPoller. No dependencies.
Contactor.Boards.Advantech/ # The Advantech ADAM-6060, ADAM-6066 and ADAM-6260 (Modbus TCP): codec, client, simulator and their adapters (AdamBoard, AdamConnection, AdamTraffic). No web/EF. README.md: their protocol
Contactor.Boards.Denkovi/   # The Denkovi smartDEN IP-16R-MT (Modbus TCP) and the DAEnetIP2 v2 kits (SNMPv1 over UDP): codecs, clients, simulators and their adapters (SmartDen*, DaenetIp2*: Board, Connection, Traffic). No web/EF. README.md: their protocols
Contactor.Boards.Devantech/ # The Devantech ETH002-B, ETH008-B, ETH1610, ETH484-B and ETH8020-B: binary TCP commands, client, simulator and its adapters (DevantechBoard, DevantechConnection, DevantechTraffic). No web/EF. README.md: its protocol
Contactor.Boards.Shelly/    # Shelly Gen2+ devices with 1 to 4 relays (HTTP RPC with SHA-256 digest): client, simulator and its adapters (ShellyBoard, ShellyConnection, ShellyTraffic). No web/EF. README.md: its protocol
Contactor.Boards.Velleman/  # The Velleman VM201 (packet codec), VM204 and VM208 (HTTP clients): their clients, simulators and adapters (Vm201Board, Vm204Connection, Vm208Traffic …). No web/EF. README.md: their protocols and routes
Contactor.Boards.Waveshare/ # The Waveshare Modbus PoE ETH relays: Modbus RTU codec, client, simulator and its adapters (WaveshareBoard, WaveshareConnection, WaveshareTraffic). No web/EF. README.md: its protocol
Contactor.Web/            # Blazor app, EF Core, background services, identity
  Persistence/              # DbContext, entities (one file per entity) and migrations; not a feature
  Resources/                # The UI's texts: one .resx per language (Texts.resx, Texts.sv.resx), sorted by key, with the marker class Texts; not a feature
  Features/<Feature>/       # One folder per feature; Features/Boards is the board catalogue, Features/<Board> what only that board has
Contactor.Simulator/      # Development: simulated boards, each on its own port, with a page to drive them and see their traffic
Contactor.Demo/           # The public demo: a proxy that gives each visitor their own app and simulator, each a process on 127.0.0.1, and the Workshop template ("Skip setup"). Dockerfile: its image (ghcr.io/gonace/contactor-demo)
Contactor.AppHost/        # Aspire: runs everything locally (aspire run)
Contactor.ServiceDefaults/ # Aspire defaults: OpenTelemetry, health checks, resilient HTTP clients
Contactor.Tests/          # The only test project
  Advantech/                # Modbus TCP codec, trace words, simulator + client against the simulator
  Boards/                   # ChannelMask, BoardPoller, board adapters
  Demo/                     # The demo's proxy, visitors' copies, supervised programs, settings, pages and the Workshop template
  Denkovi/                  # Modbus TCP and SNMPv1 codecs, trace words, simulators + clients against the simulators
  Devantech/                # Command bytes and layouts, trace words, simulator + client against the simulator
  Shelly/                   # RPC JSON, digest, trace words, simulator + client against the simulator
  Simulator/                # The simulator's boards and page
  Velleman/                 # VM201 codec, VM204 and VM208 JSON, login, trace words, connections + clients against the simulator
  Waveshare/                # Modbus codec, trace words, simulator + client against the simulator
  Web/                      # Scheduling, auto-off, daylight saving, identity
install/
  install.sh                # The one-command install (curl … | sh), the only file attached to each GitHub Release
  compose.yml               # The generic compose file it puts in /opt/contactor; in the image (/app/install), read from there
  contactor-updater.sh      # The update helper on the host ("Update now"), with its .service and .path units; in the image (/app/updater), read from there
deploy/
  demo/compose.yml          # The public demo, on a network with no route out, and its forwarder on port 8080 for an existing tunnel's connector
  demo/Caddyfile            # The forwarder's settings: only to the demo, passing on X-Forwarded-Proto
  cosign/                   # Public signing keys and how to make them
docs/DEMO.md                # Hosting the public demo
docs/ARCHITECTURE.md        # How the app is built and what it does: architecture, boards, data model, rules, screens, operations
docs/screenshots/           # The README's screenshots, taken by scripts/screenshots/take.mjs
scripts/
  screenshots/take.mjs      # Takes them: simulator, app and made-up data on a temporary database, Playwright
  release-version.sh        # YYYY.M.D[.N]
  images-build.sh           # Build and push an image with docker buildx; prints the digest to sign
  images-sign.sh            # cosign, by digest
  updater-test.sh           # Tests the update helper for real: Docker, a local registry, a test key, stand-in images
LICENSE                     # MIT
THIRD-PARTY-NOTICES.md      # Others' work we include or bundle; their licence texts in licenses/ (in the image: /licenses)
.github/workflows/build.yml # Build & Test: build + test (pull requests, merges to main, by hand)
.github/workflows/images.yml # Release: publish and sign the images, the app's and the demo's, and attach install.sh to the release (by hand)
.github/release.yml         # The releases' notes: merged pull requests, those labelled "internal" left out
.github/pull_request_template.md # What every pull request description has
.agents/writing.md          # How to write pull requests, issues and comments
.agents/review.md           # The independent review's checklist
.claude/agents/code-reviewer.md # The reviewer as a Claude Code agent
```

---

## Adding a relay board

- **The same situation, the same behaviour.** When a board has the same situation as a board the app already supports, give it the same behaviour, so two boards never act differently. For example, a rule on the board that switches several relays at once works like the Shelly's schedules. Ask the user when the situation is new. This is about behaviour, not the protocol: a gap in the manual still means stop and ask.
- **Protocol from the manufacturer's manual only.** Name the manual, its date and the section for every command and answer format, in the board's README. Never guess a format, and never mix two interfaces. If something isn't documented, stop and ask.
- **The simulator speaks the manual's format, not the client's.** Each board's tests include the manual's own example answer, byte for byte, parsed by the client.
- **Switch one relay without touching the others.** Never read a whole port, change a bit and write it back: a change at the board in between would be overwritten. Use the board's per-relay command; if it has none, ask.
- **Keep the `IRelayBoard` contract,** as the existing boards do:
  - return the relays the board took, and 0 for an empty mask;
  - keep sending the other relays when one fails ("All off" and the interlock depend on it);
  - reopen the connection after a failure instead of staying closed;
  - outside `RunCommandAsync`, a refusal is a `BoardNotConnectedException`.
- **State `OwnRules`** in the model's capabilities: whether the board has rules of its own that switch relays, and whether the app can read them and turn them off, from the manual. Never change the board's own rules without a person's press.
- **"Connected" only after the board's first valid answer.** A wrong password says so in plain words, through `LoginProblem` and `LoginHint`.
- **Bound every input:** a size limit on every answer, a length check on every packet. The simulator survives malformed packets and a dropped connection.
- **Time through `TimeProvider`:** timeouts in the code, and a fake clock in the tests. Never a real-time wait.
- **One project per manufacturer** (`Contactor.Boards.<Manufacturer>`), one file per type with the model as prefix, tests in `Contactor.Tests/<Manufacturer>/`. Copy the structure of the closest existing board.
- **Everything a board needs:** the catalogue, the simulator, `SimulatedBoardsTests`, texts in English and Swedish resx, `docs/ARCHITECTURE.md`, the README's board table, `appsettings.json`, the structure in this file, and a list in the board's README of what to check on a real board.
- **Prove it:** a Release build with 0 warnings (warnings are errors) and every test passing, run on the pushed commit. Paste the real output in the pull request. Never write "0 warnings" or "tests pass" without running them.
- **Start from the latest `main`.** A pull request that touches the same files as another open one is stacked on it.

## Testing

- **Test what can switch a relay or get a board wrong; nothing else needs a test.** The suite must stay fast and steady.
  - **Must have tests:**
    - the safety rules above: the interlock, nothing queued, nothing sent without a press;
    - each board's protocol, byte for byte against the manual, and its `IRelayBoard` contract;
    - scheduling, auto-off and daylight saving;
    - the board's own rules, logins, identity and data that is saved or migrated.
    - what the simulator emulates: each simulated board answers as the manual says, and its power cut, own rules and failures behave as the real board's would.
  - **No new tests needed, a screenshot is enough:**
    - layout and looks;
    - the simulator's page itself (its controls and layout), which only developers use;
    - texts beyond the existing resx checks.
  - **Fewer, sharper tests:** one test per behaviour, not one per variation of the same path.
  - **Never a test that waits on real time** or counts events that arrive at their own pace: it is flaky on a busy machine. Drive time with a fake `TimeProvider`, and wait for a condition. Rewrite or remove a test like that when you meet it.
- **Codec:** encode/decode, checksum, the verified example (`02 06 4F 01 A8 03`, in `Contactor.Boards.Velleman/README.md`), split and merged TCP packets, and an invalid checksum.
- **Client against the simulator:** login with and without auth, wrong password, pushed status, reconnect after a disconnect.
- **Scheduling:** with an injected `TimeProvider`. Test weekdays, over midnight, both daylight saving changes, overlaps and the reconciliation at start.
- **Auto-off:** that it triggers, and not for relays turned on by a schedule.
- **Identity:** valid and invalid JWT, wrong `aud`, and that the app refuses to start on a sign-in that doesn't fit its way in.
- **CI:** the Build & Test workflow runs `dotnet build` and `dotnet test` on every pull request to `main`, on every merge into `main` and by hand, and shows the results as a "Tests" check (`dorny/test-reporter`). A new push to a pull request cancels its older run. Run the related tests locally before pushing.
- **While working, run only the related tests:** the tests you wrote and the tests of the code you changed (filter on those test classes or that folder's namespace), never the complete suite. The complete suite runs once, as the gate below, on the final commit. For a stack, it runs once on the top pull request, which holds every part below it.
- **While the Build & Test workflow is disabled** (to save Actions minutes during heavy development): ignore the missing GitHub checks and don't bring them up. The gate is a full local run instead: a Release build and every test on a clean checkout of the pull request's latest commit (for a stack, the top pull request's, once each part has the branch below it merged in and the bottom part the latest `main`), with the commit and the result in the pull request's description.
- **Screenshots:** the README's pictures (`docs/screenshots/`) are taken by `node scripts/screenshots/take.mjs` (Node 22 or later, Playwright with Chromium): it starts the simulator and the app on a temporary database with made-up data and photographs the screens. Take them again when a screen they show changes.

## Workflow

- The user drives. Never decide the next step yourself.
- Ask the user only what you can't decide yourself from the code, these rules and the project's goals. The most important goal is simplicity for people who aren't technical (see the top of this file). Don't ask about that.
- Before writing code, explain what you plan to implement (files, types, behaviour) and wait for the user's tweaks and approval.
- **Scope is locked at the approved plan.** Anything outside it becomes its own issue, never more work in the same pull request. Only the user widens the scope.
- On your own working branch, commit and push freely, and keep its pull request up to date. No permission is needed for that.
- Never commit or push to `main`, and never merge a pull request, without explicit permission from the user. Permission to merge is explicit only when the user tells you to merge and names the pull requests. If it is unclear whether the user will merge themselves, ask.
- Work in small, reviewable chunks: one branch and one pull request per chunk.
- Before every push, review your own diff as a hostile reviewer would and fix what you find, with a test where the rules under Testing ask for one: crashes, unhandled input (empty, huge, negative, malformed), leaked subscriptions and timers, races, the safety rules above, these conventions, and stale comments or docs. Never skip this to save time or tokens; keep scope small instead.
- **Independent review:** before a pull request is ready, a reviewer that did not write the code reviews its whole diff by the checklist in `.agents/review.md` (in Claude Code, the `code-reviewer` agent in `.claude/agents/`), knowing only the diff, the surrounding code and these rules, not the author's notes. Every bug, safety and security problem in the pull request's own changes, or caused or made worse by them, is fixed before the merge, with a test where the rules under Testing ask for one, and the reviewer checks the fixes once more. At most two review rounds: one full review, then one check of only the fixes. What is still open after that goes to the user as a decision, not a third round. What the reviewer finds outside the pull request's scope becomes its own issue. A finding the author disagrees with, or a judgement call, goes to the user. The remaining nitpicks of a pull request go together into one follow-up issue, "Nitpicks from the review of PR #N".
- A pull request is **ready to merge** only when all of these hold for its latest commit:
  - the independent review covers the latest commit, and nothing it found is left open;
  - every review thread is resolved, except a thread whose finding the user explicitly decided not to act on, with that decision replied on the thread;
  - it has no `needs attention` label;
  - GitHub shows every check done and green; while the Build & Test workflow is disabled, the full local run (see Testing) on that commit passed instead, with the result in the pull request's description. In a stack, the top pull request's run counts for the parts below it, and each of them names it in its description.
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

## Conventions

- Nullable on, `TreatWarningsAsErrors` in Release.
- Time: inject `TimeProvider`. Never use `DateTime.Now` directly.
- Log with `ILogger` and structured fields. Never log the VM201 password.
- `InternalsVisibleTo` to `Contactor.Tests` only for a limit a test must lower (e.g. `ShellyClient.NonceLimit`), with a comment saying why.
- Everything in English (see Languages).
