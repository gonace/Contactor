# Independent review

This is the checklist for the independent review of a pull request on Contactor: a web app (Blazor Server, .NET,
SQLite) that switches the relays on an Ethernet relay board from a phone, tablet or computer, for people who aren't
technical. The relays drive real machines: fan heaters, a compressor, ventilation. The reviewer did not write the
code it reviews and doesn't know the author's reasoning: it judges the diff on its own. Any agent can do the review
by this file; in Claude Code it is the `code-reviewer` agent in `.claude/agents/`.

## What to read first

- `AGENTS.md`: how to work in this repository, the safety rules and the conventions. Review against them.
- `docs/ARCHITECTURE.md`: the architecture and the domain rules. Read the sections the change touches, and review
  against them.
- For a change to a board, its `Contactor.Boards.<Model>/README.md`: the board's protocol or routes, and what to check on a real board.
- `.agents/writing.md`, for how to write the report.
- The whole diff you're given (`git diff origin/<base>...origin/<branch>`, after fetching both with
  `git fetch origin <name>:refs/remotes/origin/<name>`), then whatever surrounding code you need to understand each
  change. Read the code before you state how something works.

## What you look for, in this order

- **Bugs:** wrong results, crashes and unhandled exceptions (one that leaves a Blazor event handler ends the page's
  live connection), off-by-one, null handling, races and thread safety (board events arrive on the board's own
  thread), leaked resources (event subscriptions not removed in `Dispose`, timers, connections), and edge cases:
  empty, huge, negative and malformed input, from a person or from the board.
- **Safety:** the domain rules in `docs/ARCHITECTURE.md` that keep machines safe (listed short in `AGENTS.md`). A
  group's interlock and start order; nothing sent or queued while the board is disconnected; nothing turned off at
  start except the stated exceptions; auto-off, schedules and run-on times as described; a relay in a group switched
  only through its group. A change that can turn on heat without the fan, or leave something on that should go off, is
  a blocker.
- **Security and privacy:** every request still goes through Cloudflare Access (no new route around it, and
  `/healthz` stays local only); the e-mail comes from the signed JWT; secrets (the board's password, the tunnel
  token) are never logged, shown or put in an event; data from the board is treated as untrusted.
- **Time:** times stored in UTC and shown in the app's time zone, schedules in local time, both daylight saving
  changes, and `TimeProvider` instead of the machine's clock.
- **Simplicity and the UI:** plain English words, big touch targets, every colour from a design token (dark mode),
  the phone, tablet and desktop layouts, real `<button>` and `<label>` elements.
- **Conventions:** `AGENTS.md`'s conventions and structure, with one test project. From `docs/ARCHITECTURE.md`:
  SQLite-specific code only in `Persistence` and `Features/Backup` (Data model), and the board only through
  `IRelayBoard`, `IBoardConnection` and `IBoardTraffic` (Boards).
- **Tests:** risky paths without a test, tests that can't fail, timing-dependent tests that will be flaky on a busy
  machine, and tests that depend on the host (its time zone, locale, clock or files).
- **Platform independence:** code and tests must behave the same on Windows, Linux and macOS. Developers run the
  tests on Windows; the app runs in a Linux container on a Raspberry Pi (arm64). Flag path separators, case
  sensitivity, line endings, file locking, time zone ids and culture.
- **Stale text:** comments, `AGENTS.md`, `docs/ARCHITECTURE.md`, `README.md`, the boards' `README.md` files and `docs/DEPLOY.md` that the change made wrong.

## How you work

- Verify, don't guess. Follow each code path; where it's cheap, prove a finding with a small reproduction (a
  temporary test) in a throwaway worktree of your own, e.g. `/home/user/wt-review-<pr>`, and remove it with its
  `bin`/`obj` folders when you're done. Mark anything you couldn't verify.
- Never edit, commit or push the code under review, and never comment on GitHub. You report; the author fixes.
- For each finding give: severity (blocker, bug, safety, security, convention, test, nit), `file:line`, what goes
  wrong with a concrete input or scenario, and the fix in one or two lines. Most severe first.
- Don't report style preferences as bugs, and don't pad the list: no findings is a valid result.
- Run only the tests you need to confirm a finding, never the full suite.
- Never put passwords, tokens, Access settings or e-mail addresses in your report.
- End with a one-line verdict: ready, or what must be fixed first.
