// Takes the README's screenshots: starts the VM201 simulator and the app on a temporary database, fills it with
// made-up example data, does a few things in the app as John (starts a group, turns on a light), and photographs the
// screens at phone size, light and dark. Needs the .NET SDK, Node 22 or later (for node:sqlite) and Playwright with
// Chromium (npm i -g playwright && npx playwright install chromium). Run from the repository root:
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
const data = mkdtempSync(join(tmpdir(), 'contactor-screenshots-'));
const database = join(data, 'contactor.db');
const children = [];

function start(project, env) {
    const child = spawn('dotnet', ['run', '--project', project, '--no-build', '--no-launch-profile'], {
        env: { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development', ...env },
        stdio: 'ignore',
    });
    children.push(child);
    return child;
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
    const relays = [
        [1, 'Fläkt – Svarven', 1],
        [2, 'Värme – Svarven', 1],
        [3, 'Kompressor', 1],
        [4, 'Ventilation – Lackrummet', 1],
        [5, 'Belysning – Förrådet', 1],
        [6, 'Relä 6', 0],
        [7, 'Relä 7', 0],
        [8, 'Relä 8', 0],
    ];
    const rename = db.prepare('UPDATE "Relay" SET "Name" = ?, "IsActive" = ? WHERE "Id" = ?');
    for (const [id, name, active] of relays) {
        rename.run(name, active, id);
    }

    const user = db.prepare('INSERT OR REPLACE INTO "User" ("Email", "DisplayName", "ShowBoardTraffic") VALUES (?, ?, 0)');
    user.run('john@example.com', 'John');
    user.run('jane@example.com', 'Jane');
    user.run('alex@example.com', 'Alex');

    db.prepare('INSERT INTO "RelayGroup" ("Id", "Name", "Icon", "MaxOnMinutes", "IsActive", "SortOrder") VALUES (1, ?, \'fan\', 120, 1, 1)')
        .run('Värmefläkt – Svarven');
    const member = db.prepare('INSERT INTO "RelayGroupMember" ("GroupId", "RelayId", "Position", "RoleName", "StartDelaySec", "RunOnSec") VALUES (1, ?, ?, ?, 3, ?)');
    member.run(1, 1, 'Fläkt', 300);
    member.run(2, 2, 'Värme', 0);
    db.prepare('INSERT INTO "GroupRuntime" ("GroupId", "Phase", "TargetLevel", "PendingOn") VALUES (1, \'idle\', 0, 0)').run();

    const schedule = db.prepare('INSERT INTO "Schedule" ("RelayId", "GroupId", "Days", "OnTime", "OffTime", "IsEnabled", "CreatedBy") VALUES (?, ?, ?, ?, ?, 1, ?)');
    schedule.run(3, null, 31, '06:30', '08:00', 'jane@example.com');
    schedule.run(null, 1, 31, '06:00', '07:30', 'alex@example.com');

    const event = db.prepare('INSERT INTO "EventLog" ("TimestampUtc", "RelayId", "GroupId", "Actor", "Action", "Details") VALUES (?, ?, ?, ?, ?, ?)');
    event.run(utc(26 * 60), 4, null, 'alex@example.com', 'off', null);
    event.run(utc(25 * 60), 5, null, 'jane@example.com', 'on', null);
    event.run(utc(24 * 60 + 30), 5, null, 'auto-off', 'off', '{"afterMinutes":120}');
    event.run(utc(6 * 60), 3, null, 'schedule', 'on', null);
    event.run(utc(4 * 60 + 30), 3, null, 'schedule', 'off', null);
    event.run(utc(3 * 60), 4, null, 'jane@example.com', 'rename', '{"from":"Relä 4","to":"Ventilation – Lackrummet"}');
    event.run(utc(90), null, 1, 'alex@example.com', 'group-create', '{"name":"Värmefläkt – Svarven","steps":["Fläkt","Värme"]}');
    db.close();
}

async function shoot(browser, colorScheme, shots) {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, colorScheme });
    for (const [name, path, act, fullPage] of shots) {
        await page.goto(base + path);
        await page.waitForFunction(() => window.Blazor !== undefined);
        await page.waitForTimeout(1200);
        if (act) {
            await act(page);
        }
        await page.screenshot({ path: join(out, `${name}${colorScheme === 'dark' ? '-dark' : ''}.png`), fullPage: !!fullPage });
    }
    await page.close();
}

try {
    execFileSync('dotnet', ['build', 'Contactor.slnx', '-v', 'q'], { stdio: 'inherit' });
    start('Contactor.Simulator', { Simulator__Port: String(boardPort) });
    start('Contactor.Web', {
        ASPNETCORE_URLS: base,
        ConnectionStrings__Default: `Data Source=${database}`,
        Backup__Folder: join(data, 'backups'),
        Vm201__Host: '127.0.0.1',
        Vm201__Port: String(boardPort),
    });
    await waitFor(async () => (await fetch(base + 'alive')).ok, 'the app');
    seed();

    const browser = await chromium.launch();
    const john = await browser.newPage({ viewport: { width: 390, height: 844 } });
    await john.goto(base);
    await john.waitForFunction(() => window.Blazor !== undefined);
    await john.waitForTimeout(1500);
    await john.click('button[aria-label="Slå på Värmefläkt – Svarven"]');
    await john.waitForSelector('button[aria-label="Stäng av Värmefläkt – Svarven"]');
    await john.waitForTimeout(5000);
    await john.click('button[aria-label="Slå på Belysning – Förrådet"]');
    await john.waitForSelector('button[aria-label="Stäng av Belysning – Förrådet"]');
    await john.close();

    const shots = [
        ['relays', '', null, false],
        ['group', 'grupp/1', null, false],
        ['new-group', 'grupp/ny', async (page) => {
            await page.fill('#name', 'Lackering');
            await page.click('.icons label:has-text("Lack")');
            await page.click('.next button');
            await page.click('button.option:has-text("RELÄ 4")');
            await page.click('button.option:has-text("RELÄ 5")');
        }, false],
        ['schedule', 'schema/1', null, false],
        ['events', 'handelser', null, false],
        ['settings', 'settings', null, false],
    ];
    await shoot(browser, 'light', shots);
    await shoot(browser, 'dark', shots.filter(([name]) => name === 'relays' || name === 'group'));
    await browser.close();
    console.log(`Screenshots in ${out}/`);
} finally {
    for (const child of children) {
        child.kill();
    }
    rmSync(data, { recursive: true, force: true });
}
