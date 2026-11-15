# Hosting the demo

The public demo gives each visitor their own copy of the app and the simulator, side by side. It runs on one machine
with Docker Compose: two containers, behind a Cloudflare tunnel. No port is opened on the router or published on the
machine.

```
Visitor ─▶ Cloudflare ─▶ Cloudflare Tunnel ─▶ cloudflared ─▶ demo:8080 ─▶ the visitor's app and simulator (127.0.0.1)
                                              (network "outside")  (network "demo": internal, no route out)
```

- **demo** (`ghcr.io/gonace/contactor-demo`): Contactor.Demo, with the app and the simulator inside. It is only on
  the network `demo`, which has no route out, and on which the machine itself has no address. A visitor can type any
  board address in their app, and nothing but cloudflared can be reached from the container.
- **cloudflared** (Cloudflare's own image): the only container that reaches the internet. It forwards the demo's
  hostname to the demo.

There is no sign-in in front: the demo is public.

## What you need

- A virtual machine on Proxmox (amd64): Debian 13 or Ubuntu 24.04, 2 CPU cores, 4 GB of memory and 20 GB of disk. The
  demo takes about 250 MB per visitor, and at most 10 visitors at once. Its container may use 3 GB in all.
- Docker Engine 28 or later with the Compose plugin (`docker compose version`). Docker 28 is the first that keeps the
  machine off the demo's network.
- A domain on Cloudflare, and a Cloudflare Zero Trust account (the free plan is enough).
- A classic GitHub token with only `read:packages`: the image is private, as the app's is.

## 1. The tunnel

1. In Cloudflare Zero Trust: **Networks → Tunnels → Create a tunnel → Cloudflared**. Name it, e.g. `contactor-demo`.
2. Copy the **token**: the long text after `--token` in the install command it shows. Don't run that command.
3. **Public hostname:** your demo's hostname, e.g. `demo.example.com`. Service: type **HTTP**, URL **`demo:8080`**.
4. Don't add an Access application: the demo is public.
5. In the domain's settings, **SSL/TLS → Edge Certificates → Always Use HTTPS**: on. Then every visitor comes in
   over HTTPS, and the demo marks their cookie Secure.

## 2. Install

On your computer, from the repository:

```sh
scp deploy/demo/compose.yml <host>:
```

On the machine:

```sh
sudo mkdir -p /opt/contactor-demo
sudo install -m 644 compose.yml /opt/contactor-demo/compose.yml
sudo bash -c 'umask 077; read -rsp "Tunnel token: " t; echo; printf "CLOUDFLARE_TUNNEL_TOKEN=%s\n" "$t" > /opt/contactor-demo/.env'
sudo docker login ghcr.io -u token
```

`docker login` asks for the GitHub token. The tunnel token is kept in `/opt/contactor-demo/.env`, which only root can
read. Never put it in the repository.

Start it:

```sh
cd /opt/contactor-demo
sudo docker compose up -d
```

The tunnel starts once the demo is healthy, after a few seconds.

**Check it:**

1. `sudo docker compose ps`: both containers say `healthy`.
2. Open your hostname. The page shows the simulator and the app, with "Skip setup: start from a template".
3. Choose **Workshop**. The app shows the relays, and switching one shows in the simulator too.

Logs: `sudo docker compose logs -f demo` and `sudo docker compose logs -f cloudflared`.

## 3. Upgrade

Each release publishes the demo's image with the app's, with the same version.

```sh
cd /opt/contactor-demo
sudo docker compose pull
sudo docker compose up -d
sudo docker image prune -f
```

The visitors' copies end when the demo starts again. When a release changes `deploy/demo/compose.yml`, copy it over
again first, as in Install.

To stay on one version, e.g. after a bad release, add `DEMO_IMAGE=ghcr.io/gonace/contactor-demo:2026.11.15` to
`/opt/contactor-demo/.env` and run `sudo docker compose up -d`. Remove the line to follow the newest again.

## Settings

The demo's limits are settings, under `environment:` of the `demo` service in `compose.yml`, e.g.
`Demo__MaxVisitors: 5`.

| Setting | Default | What it is |
|---|---|---|
| `Demo__MaxVisitors` | `10` | Visitors at once. The next one gets "The demo is busy right now". |
| `Demo__IdleTimeout` | `00:15:00` | Time without a click or a key before the page asks whether the visitor is still there. |
| `Demo__IdleWarning` | `00:02:00` | How long the page counts down before the copy ends. |
| `Demo__MaxLifetime` | `02:00:00` | How long a copy lasts at most. |
| `Demo__MaxBoards` | `20` | Boards per visitor in the simulator. |
| `Demo__TimeZone` | `Europe/Stockholm` | The copies' time zone, for their schedules and the Workshop template. |
| `Demo__TunnelAddress` | `172.30.60.2` (in `compose.yml`) | cloudflared's address on the demo's network: only it may say that the visitor came in over HTTPS. |

With more visitors, give the container more memory too (`mem_limit`, about 250 MB per visitor and 500 MB more).

**If the network clashes:** when `docker compose up` says that the pool overlaps with another, the subnet
`172.30.60.0/28` is in use on the machine. Pick another, e.g. `172.30.61.0/28`, and change the subnet, the
`ip_range`, cloudflared's `ipv4_address` and `Demo__TunnelAddress` in `compose.yml` to match.
