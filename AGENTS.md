# Contactor – the relay app

Contactor is an open-source mobile web app for controlling the 8 relays on a **Velleman VM201** ethernet relay board from a phone, e.g. in a workshop, where the relays drive fan heaters, a compressor, ventilation, lighting and more. It started as the app for one workshop, run by a licensed electrician and a few friends.

The users are not technical. **Simplicity comes before features**: big buttons, plain words, no menus that aren't needed.

The screens are described in `docs/ARCHITECTURE.md`; the running app is the reference for layout, texts, colours and sizes.

**How the app is built and what it does** is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): the architecture, the boards, the simulator, the data model, the domain rules, identity, the screens, configuration and operations. Read the sections a change touches before you change it.

## Safety rules every change must keep

The relays drive real machines. Each rule is told in full in its section of `docs/ARCHITECTURE.md`.

- **The board is the source of truth.** The app never stores relay state. ([Boards](docs/ARCHITECTURE.md#boards-contactorboards), [Data model](docs/ARCHITECTURE.md#data-model-sqlite))
- **Nothing is queued.** While the board is disconnected, nothing is sent or queued for later. ([Manual control](docs/ARCHITECTURE.md#manual-control), [Relay groups](docs/ARCHITECTURE.md#relay-groups))
- **A group's interlock is never bypassed.** A relay may only be on while every relay above it is, and a relay in a group is only switched through its group. ([Relay groups](docs/ARCHITECTURE.md#relay-groups))
- **Nothing is turned off at start,** except auto-off times that have already passed and a schedule's missed OffTime for a relay that schedule turned on. ([Scheduling](docs/ARCHITECTURE.md#scheduling))
- **Secrets are never logged or shown:** the board's password, the tunnel token and the Access settings. They are never in the repository or the image. ([Architecture](docs/ARCHITECTURE.md#architecture), [Configuration](docs/ARCHITECTURE.md#configuration-appsettingsjson))
- **Every request goes through Cloudflare Access.** `/healthz` needs no JWT and answers localhost only. ([Identity](docs/ARCHITECTURE.md#identity-cloudflare-access))
- **Every colour comes from a design token,** never a hex value of its own, so dark mode works on every page. ([Design tokens](docs/ARCHITECTURE.md#design-tokens))

---

## Languages

- **Everything is in English:** the UI, code, comments, log messages, tests, commit messages, pull requests, issues, `README.md` and this file. Translations of the UI may come later.

---

## Stack and structure

- .NET 10 (LTS), C#, Blazor Server (interactive server rendering), EF Core + `Microsoft.EntityFrameworkCore.Sqlite`
- No JavaScript framework. No external CSS framework; one small stylesheet of our own, following the design tokens in `docs/ARCHITECTURE.md`.
- Target: one container image (`ghcr.io/gonace/contactor`, linux/arm64 and linux/amd64), built from the `Dockerfile` on Ubuntu chiseled-extra, which includes the time zone data the schedules need, with `cloudflared` copied in; everything it keeps is in `/data`. Run with Docker Compose on the Raspberry Pi.
- Local development with Aspire 13.5.4, pinned on purpose: later releases hang at startup on Windows. Its dashboard's advisories GHSA-f7vh-h5g3-7jrh (its cookie reaching a container opened from it) and GHSA-4g86-54fm-h9h8 (anyone who reaches it can delete its telemetry) are suppressed in the AppHost only, which runs no containers and serves the dashboard on localhost; the dashboard isn't in the image. Raise the version once a release starts on Windows (issue #18). The AppHost starts the simulator (a VM201 on port 9760, and its page linked from the dashboard) and the web app against it.

No `src/` or `tests/` folders: every project sits directly in the repository root. All tests live in **one** test project, `Contactor.Tests`, with a folder per area (`Vm201/`, `Web/` …). Don't create more test projects.

```
Contactor.slnx
Dockerfile                  # The image: the app and cloudflared, for linux/arm64 and linux/amd64
global.json                 # SDK version and test runner (Microsoft.Testing.Platform)
Directory.Build.props       # Shared build settings
Directory.Packages.props    # Central package management: every package version is here
Contactor.Boards/           # What any relay board is to the app: IRelayBoard, IBoardConnection, IBoardTraffic, BoardModel, BoardPoller. No dependencies.
Contactor.Boards.Devantech/ # The Devantech ETH002-B, ETH008-B, ETH484-B and ETH8020-B: binary TCP commands, client, simulator and its adapters (DevantechBoard, DevantechConnection, DevantechTraffic). No web/EF. README.md: its protocol
Contactor.Boards.Vm201/     # The VM201: packet codec, client, simulator and its adapters (Vm201Board, Vm201Connection, Vm201Traffic). No web/EF. README.md: its protocol
Contactor.Boards.Vm204/     # The VM204: HTTP client, simulator and its adapters (Vm204Board, Vm204Connection, Vm204Traffic). No web/EF. README.md: its routes
Contactor.Boards.Vm208/     # The VM208: HTTP client, simulator and its adapters (Vm208Board, Vm208Connection, Vm208Traffic). No web/EF. README.md: its routes
Contactor.Boards.Waveshare/ # The Waveshare Modbus PoE ETH relays: Modbus RTU codec, client, simulator and its adapters (WaveshareBoard, WaveshareConnection, WaveshareTraffic). No web/EF. README.md: its protocol
Contactor.Web/            # Blazor app, EF Core, background services, identity
  Persistence/              # DbContext, entities (one file per entity) and migrations; not a feature
  Features/<Feature>/       # One folder per feature; Features/Boards is the board catalogue, Features/<Board> what only that board has
Contactor.Simulator/      # Development: simulated boards, each on its own port, with a page to drive them and see their traffic
Contactor.AppHost/        # Aspire: runs everything locally (dotnet run --project Contactor.AppHost)
Contactor.ServiceDefaults/ # Aspire defaults: OpenTelemetry, health checks, resilient HTTP clients
Contactor.Tests/          # The only test project
  Boards/                   # ChannelMask, BoardPoller, board adapters
  Devantech/                # Command bytes and layouts, trace words, simulator + client against the simulator
  Simulator/                # The simulator's boards and page
  Vm201/                    # Codec + client against the simulator
  Vm204/                    # JSON, login, trace words + client against the simulator
  Vm208/                    # JSON, trace words, connection + client against the simulator
  Waveshare/                # Modbus codec, trace words, simulator + client against the simulator
  Web/                      # Scheduling, auto-off, daylight saving, identity
deploy/
  compose.yml               # The app (cloudflared inside) on the Raspberry Pi; no published ports
  install.sh                # Run on the Pi: settings to /opt/contactor/.env, data folder, ghcr.io login, first start
  upgrade.sh                # Pulls, checks the cosign signature, restarts, waits until healthy
  autoupdate-install.sh     # systemd timer: upgrade every night 03:00–04:00
  cosign/                   # Public signing keys and how to make them
docs/ARCHITECTURE.md        # How the app is built and what it does: architecture, boards, data model, rules, screens, operations
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

## Testing

- **Codec:** encode/decode, checksum, the verified example (`02 06 4F 01 A8 03`, in `Contactor.Boards.Vm201/README.md`), split and merged TCP packets, and an invalid checksum.
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

## Conventions

- Nullable on, `TreatWarningsAsErrors` in Release.
- Time: inject `TimeProvider`. Never use `DateTime.Now` directly.
- Log with `ILogger` and structured fields. Never log the VM201 password.
- Everything in English (see Languages).
