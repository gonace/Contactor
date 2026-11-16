// Takes the README's screenshots: starts the VM201 simulator and the app on a temporary database, fills it with
// made-up example data, does a few things in the app as John (starts a group, turns on a light), and photographs the
// screens at phone size, light and dark. It also photographs a step of the setup guide on the first start and, last,
// the relays in Swedish. Needs the .NET SDK, Node 22 or later (for node:sqlite) and Playwright with Chromium
// (npm i -g playwright && npx playwright install chromium). Run from the repository root:
//
//   node scripts/screenshots/take.mjs
//
// The pictures land in docs/screenshots/.
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { DatabaseSync } from 'node:sqlite';

// Through require, so a Playwright installed globally (npm i -g) is found too.
const { chromium } = createRequire(import.meta.url)('playwright');

const out = 'docs/screenshots';
const webPort = 5291;
const boardPort = 9761;
const base = `http://127.0.0.1:${webPort}/`;
const simulatorBase = 'http://127.0.0.1:5292/';
const data = mkdtempSync(join(tmpdir(), 'contactor-screenshots-'));
const database = join(data, 'contactor.db');
const children = [];
// A phone, and English with a 24-hour clock in the browser's own fields (the time fields of a schedule).
const phone = { viewport: { width: 390, height: 900 }, deviceScaleFactor: 2, locale: 'en-GB' };

// The built app itself rather than `dotnet run`, so stopping the child stops the app. With `log`, each line it writes
// goes to that function (the first start's setup code is in it).
function start(project, env, log) {
    const child = spawn('dotnet', [join('bin', 'Debug', 'net10.0', `${project}.dll`)], {
        cwd: project,
        env: { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development', ...env },
        stdio: ['ignore', log ? 'pipe' : 'ignore', 'ignore'],
    });
    if (log) {
        let rest = '';
        child.stdout.setEncoding('utf8');
        child.stdout.on('data', (chunk) => {
            const lines = (rest + chunk).split('\n');
            rest = lines.pop();
            lines.forEach(log);
        });
    }
    children.push(child);
    return child;
}

async function stop(child) {
    if (child.exitCode === null) {
        const exited = new Promise((resolve) => child.once('exit', resolve));
        child.kill();
        await exited;
    }
}

async function waitFor(check, what) {
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
        try {
            if (await check()) {
                return;
            }
        } catch {
            // Not up yet.
        }
        await new Promise((resolve) => setTimeout(resolve, 250));
    }
    throw new Error(`Timed out waiting for ${what}.`);
}

// "yyyy-MM-dd HH:mm:ss" in UTC, as EF Core stores a DateTime in SQLite.
function utc(minutesAgo) {
    return new Date(Date.now() - minutesAgo * 60_000).toISOString().replace('T', ' ').slice(0, 19);
}

function seed() {
    const db = new DatabaseSync(database);
    // By channel: a relay's id is only the database's key.
    const relays = [
        [1, 'Fan 1', 1],
        [2, 'Fan 2', 1],
        [3, 'Heater 1', 1],
        [4, 'Heater 2', 1],
        [5, 'Compressor', 1],
        [6, 'Ventilation – Paint shop', 1],
        [7, 'Lighting – Paint shop', 1],
        [8, 'Relay 8', 0],
    ];
    const rename = db.prepare('UPDATE "Relay" SET "Name" = ?, "IsActive" = ? WHERE "BoardId" = 1 AND "Channel" = ?');
    for (const [channel, name, active] of relays) {
        rename.run(name, active, channel);
    }

    const relayAt = db.prepare('SELECT "Id" FROM "Relay" WHERE "BoardId" = 1 AND "Channel" = ?');
    const relay = (channel) => relayAt.get(channel).Id;

    const user = db.prepare('INSERT OR REPLACE INTO "User" ("Email", "DisplayName", "ShowBoardTraffic") VALUES (?, ?, 0)');
    user.run('jane@example.com', 'Jane');
    user.run('alex@example.com', 'Alex');

    db.prepare('INSERT INTO "RelayGroup" ("Id", "BoardId", "Name", "Icon", "MaxOnMinutes", "IsActive", "SortOrder") VALUES (1, 1, ?, \'fan\', 120, 1, 1)')
        .run('Fan heaters');
    // In steps: both fans first, then both heaters. The heaters go off first, and the fans run on for 5 minutes.
    const step = db.prepare('INSERT INTO "RelayGroupStep" ("Id", "GroupId", "Position", "Name", "StartDelaySec", "RunOnSec") VALUES (?, 1, ?, ?, 3, ?)');
    step.run(1, 1, 'Fans', 300);
    step.run(2, 2, 'Heaters', 0);
    const member = db.prepare('INSERT INTO "RelayGroupMember" ("GroupId", "RelayId", "StepId", "Order") VALUES (1, ?, ?, ?)');
    member.run(relay(1), 1, 1);
    member.run(relay(2), 1, 2);
    member.run(relay(3), 2, 1);
    member.run(relay(4), 2, 2);
    db.prepare('INSERT INTO "GroupRuntime" ("GroupId", "Phase", "TargetLevel", "PendingOn") VALUES (1, \'idle\', 0, 0)').run();

    // The board's input, named, and known since its change below (see "Once the board has answered").
    db.prepare('UPDATE "Input" SET "Name" = \'Door – Workshop\' WHERE "BoardId" = 1 AND "Channel" = 1').run();
    db.prepare('UPDATE "BoardRuntime" SET "LongGapEndedUtc" = NULL WHERE "BoardId" = 1').run();
    // The door turns the fan heaters off when it opens: a line on the group's card.
    db.prepare('INSERT INTO "InputAction" ("InputId", "RelayId", "GroupId", "WhenClosed", "WhenOpened") VALUES (?, NULL, 1, NULL, 0)')
        .run(db.prepare('SELECT "Id" FROM "Input" WHERE "BoardId" = 1 AND "Channel" = 1').get().Id);

    const schedule = db.prepare('INSERT INTO "Schedule" ("RelayId", "GroupId", "Days", "OnTime", "OffTime", "IsEnabled", "CreatedBy") VALUES (?, ?, ?, ?, ?, 1, ?)');
    schedule.run(relay(5), null, 31, '06:30', '08:00', 'jane@example.com');
    schedule.run(null, 1, 31, '06:00', '07:30', 'alex@example.com');

    const event = db.prepare('INSERT INTO "EventLog" ("TimestampUtc", "BoardId", "RelayId", "GroupId", "Actor", "Action", "Details") VALUES (?, 1, ?, ?, ?, ?, ?)');
    event.run(utc(26 * 60), relay(6), null, 'alex@example.com', 'off', null);
    event.run(utc(25 * 60), relay(7), null, 'jane@example.com', 'on', null);
    event.run(utc(24 * 60 + 30), relay(7), null, 'auto-off', 'off', '{"afterMinutes":120}');
    event.run(utc(6 * 60), relay(5), null, 'schedule', 'on', null);
    event.run(utc(4 * 60 + 30), relay(5), null, 'schedule', 'off', null);
    event.run(utc(3 * 60), relay(6), null, 'jane@example.com', 'rename', '{"from":"Relay 6","to":"Ventilation – Paint shop"}');
    db.prepare('INSERT INTO "EventLog" ("TimestampUtc", "BoardId", "InputId", "Actor", "Action", "Details") VALUES (?, 1, ?, \'external\', \'input-changed\', \'{"on":false}\')')
        .run(utc(5 * 60), db.prepare('SELECT "Id" FROM "Input" WHERE "BoardId" = 1 AND "Channel" = 1').get().Id);
    event.run(utc(90), null, 1, 'alex@example.com', 'group-create', '{"name":"Fan heaters","steps":["Fans","Heaters"]}');
    db.close();
}

// Past the setup guide with a VM201, only on this network and with no sign-in, so the app runs that board from its
// next start and opens on the relays. The shared person is called John, so the screens greet someone by name.
function setUp() {
    const db = new DatabaseSync(database);
    db.prepare('INSERT OR REPLACE INTO "Installation" ("Id", "SetupDoneUtc", "AccessType", "SignInType") VALUES (1, ?, \'local\', \'none\')').run('2026-11-01 08:00:00');
    db.prepare('INSERT INTO "Board" ("Id", "Name", "Type", "SortOrder") VALUES (1, \'Velleman VM201\', \'vm201\', 1)').run();
    db.prepare('INSERT OR REPLACE INTO "User" ("Email", "DisplayName", "ShowBoardTraffic") VALUES (\'someone@contactor.invalid\', \'John\', 0)').run();
    db.close();
}

// A shot's height is the phone's, a number, or 'content': as tall as the page, so the tab bar covers nothing.
async function shoot(browser, colorScheme, shots) {
    const page = await browser.newPage({ ...phone, colorScheme });
    for (const [name, path, act, fullPage, height] of shots) {
        await page.setViewportSize({ width: phone.viewport.width, height: typeof height === 'number' ? height : phone.viewport.height });
        await page.goto(base + path);
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        if (act) {
            await act(page);
        }
        if (height === 'content') {
            await page.setViewportSize({ width: phone.viewport.width, height: await page.evaluate(() => document.documentElement.scrollHeight) });
            await page.waitForTimeout(300);
        }
        await page.screenshot({ path: join(out, `${name}${colorScheme === 'dark' ? '-dark' : ''}.png`), fullPage: !!fullPage });
    }
    await page.close();
}

try {
    // Without the commit in the version: the shots can't name the commit they end up in, so they show none.
    execFileSync('dotnet', ['build', 'Contactor.slnx', '-v', 'q', '-p:IncludeSourceRevisionInInformationalVersion=false'], { stdio: 'inherit' });
    start('Contactor.Simulator', {
        ASPNETCORE_URLS: simulatorBase,
        Simulator__Boards__0__Type: 'vm201',
        Simulator__Boards__0__Port: String(boardPort),
    });
    const web = {
        ASPNETCORE_URLS: base,
        ConnectionStrings__Default: `Data Source=${database}`,
        Backup__Folder: join(data, 'backups'),
        Vm201__Host: '127.0.0.1',
        Vm201__Port: String(boardPort),
    };

    // The first start makes the database; the board is chosen at start, so the app starts again to run it.
    let code = null;
    const first = start('Contactor.Web', web, (line) => {
        code ??= /The setup code is ([A-Z0-9-]+)/.exec(line)?.[1] ?? null;
    });
    await waitFor(async () => (await fetch(base + 'alive')).ok, 'the app');
    await waitFor(() => code !== null, 'the setup code');

    // A British English browser, so the time fields show a 24-hour clock as the app does.
    const browser = await chromium.launch({ args: ['--lang=en-GB'], env: { ...process.env, LANG: 'en_GB.UTF-8' } });
    // The setup guide, as a new installation opens on it: the code from the log, the language as it is, then the way
    // in. Nothing is chosen there, so the database is as the first start left it.
    {
        const page = await browser.newPage(phone);
        await page.goto(base + 'setup/code');
        await page.fill('#code', code);
        await page.click('.next button[type="submit"]');
        await page.waitForURL(/\/setup$/);
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        await page.click('.next button:not(.link)');
        await page.waitForSelector('text=Only on this network');
        await page.waitForTimeout(500);
        await page.screenshot({ path: join(out, 'setup.png') });
        await page.close();
    }

    setUp();
    await stop(first);
    start('Contactor.Web', web);
    await waitFor(async () => (await fetch(base + 'alive')).ok, 'the app');
    // Once the board has answered: its first contact on a new database counts as the end of a long gap, which would
    // make the input's seeded time unknown (an installation that has run for a while knows it).
    await waitFor(() => {
        const db = new DatabaseSync(database, { readOnly: true });
        try {
            return !!db.prepare('SELECT "LastContactUtc" FROM "BoardRuntime" WHERE "BoardId" = 1').get()?.LastContactUtc;
        } finally {
            db.close();
        }
    }, 'the board');
    seed();

    const john = await browser.newPage({ viewport: { width: 390, height: 844 } });
    await john.goto(base);
    await john.waitForFunction(() => window.Blazor !== undefined);
    await john.waitForTimeout(1500);
    await john.click('button[aria-label="Turn on Fan heaters"]');
    await john.waitForSelector('button[aria-label="Turn off Fan heaters"]');
    await john.waitForTimeout(5000);
    await john.click('button[aria-label="Turn on Lighting – Paint shop"]');
    await john.waitForSelector('button[aria-label="Turn off Lighting – Paint shop"]');
    await john.close();

    const shots = [
        // The whole page, so the tab bar doesn't hide the Inputs card.
        ['relays', '', null, false, 'content'],
        // The steps as cards, right under the top of the screen, on a phone just short enough to end with them.
        ['group', 'group/1', async (page) => {
            await page.evaluate(() => {
                const heading = [...document.querySelectorAll('h2, h3')].find((h) => h.textContent.trim() === 'Steps in start order');
                window.scrollTo(0, heading.getBoundingClientRect().top + window.scrollY - 16);
            });
            await page.waitForTimeout(300);
        }, false, 844],
        ['new-group', 'group/new', async (page) => {
            await page.fill('#name', 'Paint shop');
            await page.click('.icons label:has-text("Paint")');
            await page.click('.next button');
            await page.click('button.option:has-text("RELAY 6")');
            await page.click('button.option:has-text("RELAY 7")');
            // The clicks scroll the page down to Relay 7; back to the top, with the step and its heading.
            await page.evaluate(() => window.scrollTo(0, 0));
            await page.waitForTimeout(300);
        }, false, 'content'],
        ['schedule', 'schedule/1', null, false],
        ['events', 'events', null, false],
        // Just tall enough for the People card to end above the tab bar, with Backups not yet showing.
        ['settings', 'settings', null, false, 974],
    ];
    await shoot(browser, 'light', shots);
    await shoot(browser, 'dark', shots.filter(([name]) => name === 'relays'));
    // The wider layouts (from 768 px a tablet, from 1024 px a desktop).
    for (const [name, path, width, height] of [
        ['relays-tablet', '', 834, 1194],
        ['relays-desktop', '', 1440, 900],
    ]) {
        const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1, locale: 'en-GB' });
        await page.goto(base + path);
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        await page.screenshot({ path: join(out, `${name}.png`) });
        await page.close();
    }

    // Last, as it is the whole app's language: Swedish, chosen in Settings, and the relays in it, with the names a
    // Swedish workshop would give them.
    {
        const db = new DatabaseSync(database);
        const rename = db.prepare('UPDATE "Relay" SET "Name" = ? WHERE "BoardId" = 1 AND "Channel" = ?');
        for (const [channel, name] of [[1, 'Fläkt 1'], [2, 'Fläkt 2'], [3, 'Värme 1'], [4, 'Värme 2'], [5, 'Kompressor'], [6, 'Ventilation – Lackrum'], [7, 'Belysning – Lackrum']]) {
            rename.run(name, channel);
        }
        db.prepare('UPDATE "RelayGroup" SET "Name" = \'Värmefläktar\' WHERE "Id" = 1').run();
        db.prepare('UPDATE "RelayGroupStep" SET "Name" = CASE "Position" WHEN 1 THEN \'Fläktar\' ELSE \'Värme\' END WHERE "GroupId" = 1').run();
        db.close();
        const page = await browser.newPage({ ...phone, locale: 'sv-SE' });
        await page.goto(base + 'settings');
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        await page.selectOption('select:has(option[value="sv"])', 'sv');
        await page.waitForFunction(() => document.documentElement.lang.startsWith('sv'));
        await page.goto(base);
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        // The whole page, as the relays in English, so the tab bar doesn't hide the Inputs card.
        await page.setViewportSize({ width: phone.viewport.width, height: await page.evaluate(() => document.documentElement.scrollHeight) });
        await page.waitForTimeout(300);
        await page.screenshot({ path: join(out, 'relays-sv.png') });
        await page.close();
    }
    await browser.close();
    console.log(`Screenshots in ${out}/`);
} finally {
    for (const child of children) {
        child.kill();
    }
    rmSync(data, { recursive: true, force: true });
}
