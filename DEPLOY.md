# Deploying Contactor

Contactor runs on a Raspberry Pi next to the relay board, with Docker Compose: the app's container and `cloudflared`,
which connects out to Cloudflare. No port is opened on the router or published on the Raspberry Pi. Everyone signs
in through Cloudflare Access on a hostname of your own.

```
Phone ─▶ Cloudflare Access ─▶ Cloudflare Tunnel ─▶ cloudflared ─▶ contactor:8080 ─▶ VM201 (TCP 9760)
                                                   (Raspberry Pi, Docker Compose)
```

You need a domain on Cloudflare and a Cloudflare Zero Trust account (the free plan is enough). Set up Cloudflare
first ([below](#cloudflare)), then the Raspberry Pi.

## 1. Prepare the Raspberry Pi

- Raspberry Pi OS Lite (64-bit) on an SD card of 16 GB or more. Take an endurance card (e.g. SanDisk High Endurance
  or Samsung PRO Endurance), made for constant writing. The app writes little, and Docker's logs are capped, but a
  card can still wear out: download a backup now and then (see Backups).
- A Pi 4 with 2 GB or more is the better choice; a Pi 3B (1 GB) works too, as long as it runs the 64-bit OS.
- In Raspberry Pi Imager's settings: a host name (e.g. `contactor`), a user with your SSH key, and SSH on.
- Boot it and check that `ssh <user>@contactor.local` works.

## 2. Install

Have these at hand from the Cloudflare setup: the **team domain**, the **AUD tag** and the **tunnel token**. Then
copy the deploy folder over and run the installer. It installs Docker if needed and asks for them and the board's
address:

```sh
scp -r deploy <pi>:contactor-deploy
ssh -t <pi> sudo contactor-deploy/install.sh
```

`-t` gives it a terminal to ask on; without one, give every value as a variable (see the top of `install.sh`). If
you set it up away from the board, any address will do: you set the right one in the app later.

If the board has a login, you can give it on the first run:
`ssh -t <pi> sudo VM201_USERNAME=… VM201_PASSWORD=… contactor-deploy/install.sh` (at most 8 characters each,
no single quote), or set it later in the app.

The settings are kept in `/opt/contactor/.env` (readable by root only); the database, its backups and the app's
keys in `/var/lib/contactor`.

**Check it:** open your hostname on your phone and sign in with the code. Reläer shows
"Ingen kontakt med reläkortet", which is right without a board. Turn on **Inställningar → Debugging**: the Debug tab
then shows the app trying to connect, so the whole chain from the phone to the Pi works.

## 3. Nightly updates

```sh
sudo contactor-autoupdate-install
```

A systemd timer runs `contactor-upgrade` every night between 03:00 and 04:00. It pulls the newest image, checks its
signature, and only then restarts the app; an unsigned image is refused and the running one is kept. Once the new
version is healthy, it removes all but the 3 newest versions, so the SD card doesn't fill up. Logs:
`journalctl -u contactor-upgrade`. To upgrade at once: `sudo contactor-upgrade`.

To go back to an earlier version, e.g. after a bad release: add `CONTACTOR_IMAGE='ghcr.io/gonace/contactor:2026.11.12'`
to `/opt/contactor/.env` and run `sudo contactor-upgrade`. It stays on that version until you remove the line
again.

When a release changes anything under `deploy/` (new settings, services or signing keys), copy the deploy folder
over again and run `install.sh`; it keeps the settings.

## 4. The board

1. In your router, give the Raspberry Pi and the VM201 **DHCP reservations**, so they keep their addresses. The
   board shows up as a Velleman device in the router's list of clients.
2. In the app, **Inställningar → Reläkortet**: the board's address, its login if it has one, and **Spara**. "Kortet
   svarar på …" shows when it answers.
3. With the **Debug** tab open, test what the board does (the open questions in `CLAUDE.md`):
   - Turn a relay on and off in the app: "→ Slå på relä n", then "← Status: relä n på".
   - Press a button on the board, or use its own web page: the change shows in Händelser as "Utanför appen".
   - **How many clients at once?** Open the board's web page while the app is connected. Does either lose its
     connection?
   - **Does it close idle connections?** Leave it for 30 minutes and look for "Anslutningen bröts".
   - **A power cut:** turn a relay on, unplug the board and plug it in again. Which state do the relays come back in?

   Take a screenshot of the Debug tab for anything that looks odd.

## Publishing a release

For maintainers. Start the Images workflow (Actions → Images → Run workflow, on `main`). Without Actions minutes, do
the same from a clean checkout of `main` on your computer, with the .NET SDK and cosign installed (it asks for a classic token with
`write:packages`; give that one 30 days):

```sh
bash scripts/images-publish.sh
```

Both first build the solution and run every test, and publish nothing if one fails. Then they build
`ghcr.io/gonace/contactor` for arm64 and x64, sign it, and tag it with the version (`2026.11.5`), `latest` and
`sha-<commit>`. Installations take it the same night.

## The board's address and login

The installer asks for the address once (and takes a login as variables). After that, change both in the app under
**Inställningar**: the app reconnects at once, shows whether the board answers, and logs who changed what (never the
password). What is saved there wins over the installer's; it lives in the database, so it comes back with a restored
backup. On a fresh install without the old database, the installer's values apply until someone saves others in the
app.

## Moving to a new Raspberry Pi (or reinstalling)

1. On the old one, if it still runs: **Inställningar → Säkerhetskopior → Gör en kopia nu**, then **Ladda ner**. You
   get a zip, e.g. `contactor-2026-11-04.zip`; unpack it on your computer (Windows: right-click → Extract All) to
   get `contactor-2026-11-04.db`.
2. Install the new one as in steps 1–3. Any board address will do: you can correct it in the app.
3. Put the copy back, so names, schedules, the event log and the board settings come along:

```sh
scp contactor-2026-11-04.db <pi>:
ssh -t <pi>
sudo docker compose -f /opt/contactor/compose.yml stop contactor
sudo install -o 1654 -g 1654 -m 0644 ~/contactor-2026-11-04.db /var/lib/contactor/contactor.db
sudo rm -f /var/lib/contactor/contactor.db-wal /var/lib/contactor/contactor.db-shm
sudo docker compose -f /opt/contactor/compose.yml start contactor
```

4. Open the app and check **Inställningar**: the board should answer. If not, fix its address there.

## Backups

The app copies its database every night at 02:30 into `/var/lib/contactor/backups/contactor-YYYY-MM-DD.db` and
keeps the latest 14. They are on the same disk: download one now and then from **Inställningar → Säkerhetskopior**
(**Ladda ner**, a zip of the newest copy) and keep it somewhere else. A copy holds everything, also the board's
password and everyone's e-mail address, so keep it like any other private file.

To restore one:

```sh
sudo docker compose -f /opt/contactor/compose.yml stop contactor
sudo cp /var/lib/contactor/backups/contactor-2026-11-04.db /var/lib/contactor/contactor.db
sudo rm -f /var/lib/contactor/contactor.db-wal /var/lib/contactor/contactor.db-shm
sudo chown 1654:1654 /var/lib/contactor/contactor.db && sudo docker compose -f /opt/contactor/compose.yml start contactor
```

## Watching the board's traffic

The VM201 keeps no log of its own. The quickest way to see what the app and the board send each other, e.g. when
testing a new board: in the app, Inställningar → Debugging → "Visa trafiken till reläkortet". It is only for you.
Each packet then shows for a few seconds at the bottom of every page (not the keep-alive), and the tab Debug lists
the latest 100 with their bytes. Nothing is stored; a restart empties the list.

To have it in the container's log as well, turn on the packet log:

```sh
echo VM201_LOG_PACKETS=true | sudo tee -a /opt/contactor/.env
sudo docker compose -f /opt/contactor/compose.yml up -d
sudo docker compose -f /opt/contactor/compose.yml logs -f contactor
```

Every packet shows as one line, `→` to the board and `←` from it, with its command letter and bytes, e.g.
`VM201 → O 02 06 4F 01 A8 03` (turn on relay 1) and `VM201 ← S 02 08 53 01 00 00 A2 03` (status: relay 1 on). The
user name and password packets show only as `U (14 bytes, hidden)` and `W (14 bytes, hidden)`. With the keep-alive
it writes a few lines every 15 seconds, so turn it off again afterwards: remove the line from `.env` and run
`sudo docker compose -f /opt/contactor/compose.yml up -d`.

## When something is wrong

`/opt/contactor` holds the secrets, so only root can open it: run compose through `sudo` with `-f`, as below,
rather than `cd` into it first.

- `sudo docker compose -f /opt/contactor/compose.yml ps` shows both containers and whether the app is healthy.
- `sudo docker compose -f /opt/contactor/compose.yml logs --tail 100 contactor` (or `cloudflared`) shows what they say.
- "Ingen kontakt med reläkortet" in the app: the app can't reach the VM201. Check the board's address under
  Inställningar (find it in the router's list of clients) and that the board answers: `nc -vz <address> 9760`.
- "Åtkomst nekad.": the request didn't come through Cloudflare Access, or the team domain or AUD tag in `.env` is
  wrong.
- No new version for a while: `journalctl -u contactor-upgrade`. "unauthorized" or "denied" from ghcr.io means the
  read token has expired; renew it as [GitHub](#github) says.
- Removing someone from the Access policy stops their next sign-in, but a page they already have open keeps working
  until its live connection drops (closing the tab, or the phone sleeping long enough). To shut them out at once,
  restart the app: `sudo docker compose -f /opt/contactor/compose.yml restart contactor`.

## Cloudflare

In Cloudflare Zero Trust (the free plan is enough):

- Your **team domain**, `<team>.cloudflareaccess.com`, is shown under Settings.
- **Tunnel** (Networks → Connectors → Cloudflare Tunnels): create one, e.g. `contactor`, with a public hostname on
  your domain, e.g. `relays.example.com` → service `HTTP`, URL `contactor:8080`. Its **tunnel token** is in the
  install command on the tunnel's page (the long string after `--token`).
- **Access application** for that hostname (Access controls → Applications, self-hosted): one-time PIN by e-mail,
  session duration 1 month, a policy with the e-mail addresses that may sign in, and Instant Auth on. Its **AUD tag**
  is under Edit → Additional settings. Add or remove people in the policy.
- **Login page** (optional): `deploy/cloudflare/access-login.html` is a Swedish login page in the app's style. It
  needs HTML templates, a paid Zero Trust feature; on the free plan, Cloudflare's own page is used.

## Signing keys

For maintainers. Every release is signed with cosign, and `contactor-upgrade` only runs an image signed with one of
the public keys in `deploy/cosign/` (also in `COSIGN_KEYS` in `deploy/upgrade.sh`). There are two key pairs, made as
`deploy/cosign/README.md` says:

- the **main key** signs every release; its contents and password are the repository secrets `COSIGN_PRIVATE_KEY` and
  `COSIGN_PASSWORD` for the Images workflow;
- the **reserve key** is kept offline.

If the main key is lost or leaks: sign with the reserve key (`scripts/images-publish.sh` asks which key file to use),
make a new main pair, replace its public half in `deploy/cosign/` and in `deploy/upgrade.sh`, update the two secrets,
and publish a release; installations take the new key the next time someone runs `install.sh`.

If you publish your own images, make your own key pairs and put their public halves in instead.

## GitHub

- **Read token for the Raspberry Pi**, only while the image is private: a classic personal access token with only
  `read:packages`, valid for a year. The installer asks for it once. When it is about to expire, make a new one the
  same way and give it to the Raspberry Pi:

  ```sh
  ssh -t <pi>
  read -rsp "GitHub token: " T && echo && printf '%s' "$T" | sudo docker login ghcr.io -u token --password-stdin; unset T
  sudo contactor-upgrade
  ```

  The last command checks that pulling works. Then delete the old token on GitHub.
- **Images workflow:** publishes with the workflow's own `GITHUB_TOKEN`, so it needs no personal token; only the two
  cosign secrets above.
