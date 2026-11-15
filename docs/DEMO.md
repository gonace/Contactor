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
- Docker Engine 28 or later with the Compose plugin, installed from Docker's own apt repository
  (https://docs.docker.com/engine/install/debian/ or .../ubuntu/), not the distribution's `docker.io` package, which is
  older (Debian 13 has 26.1). Docker 28 is the first that keeps the machine off the demo's network: an older one would
  let a visitor reach the machine's own services, such as SSH. `upgrade.sh` refuses to start the demo on one.
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
scp deploy/demo/compose.yml deploy/demo/upgrade.sh <host>:
```

On the machine:

```sh
sudo mkdir -p /opt/contactor-demo
sudo install -m 644 compose.yml /opt/contactor-demo/compose.yml
sudo install -m 755 upgrade.sh /opt/contactor-demo/upgrade.sh
sudo bash -c 'umask 077; read -rsp "Tunnel token: " t; echo; printf "CLOUDFLARE_TUNNEL_TOKEN=%s\n" "$t" > /opt/contactor-demo/.env'
sudo docker login ghcr.io -u token
```

`docker login` asks for the GitHub token. The tunnel token is kept in `/opt/contactor-demo/.env`, which only root can
read. Never put it in the repository.

Start it:

```sh
sudo /opt/contactor-demo/upgrade.sh
```

It refuses a Docker older than 28, pulls the image, checks its signature with cosign by digest (the public keys in
`deploy/cosign/`, as the Raspberry Pi does) and prepares exactly that image. Before it starts anything, it checks that
the machine has no address on the demo's network, neither IPv4 nor global IPv6, and stops the demo if it has or if it
can't tell. Always start the demo with it, not with `docker compose up`. The tunnel starts once the demo is healthy,
after a few seconds.

**Check it:**

1. `cd /opt/contactor-demo && sudo docker compose ps`: both containers say `healthy`.
2. `ip -br addr | grep 172.30.60.` prints nothing: the machine has no IPv4 address on the demo's network (with another
   subnet, grep for that).
3. `ip -6 -o addr show dev br-$(sudo docker network inspect -f '{{.Id}}' contactor-demo_demo | cut -c1-12) scope global`
   prints nothing: the machine has no IPv6 address there either.

   If step 2 or 3 prints a line, stop the demo at once (`sudo docker compose down`): a visitor could reach the machine.
   Upgrade Docker as in What you need, keep `enable_ipv6: false` in `compose.yml`, and start it again.
4. Open your hostname. The page shows the simulator and the app, with "Skip setup: start from a template".
5. Choose **Workshop**. The app shows the relays, and switching one shows in the simulator too.

Logs: `sudo docker compose logs -f demo` and `sudo docker compose logs -f cloudflared`.

## 3. Upgrade

Each release publishes the demo's image with the app's, with the same version.

```sh
sudo /opt/contactor-demo/upgrade.sh
```

It only starts an image signed with our key; on an unsigned or wrongly signed one, the demo keeps running the image it
has. The visitors' copies end when the demo starts again. When a release changes `deploy/demo/compose.yml` or
`deploy/demo/upgrade.sh`, copy them over again first, as in Install.

To stay on one version, e.g. after a bad release, add `DEMO_IMAGE=ghcr.io/gonace/contactor-demo:2026.11.15` to
`/opt/contactor-demo/.env` and run `upgrade.sh`. Remove the line to follow the newest again.

## Settings

The demo's limits are settings, under `environment:` of the `demo` service in `compose.yml`, e.g.
`Demo__MaxVisitors: 5`. Run `upgrade.sh` after a change.

| Setting | Default | What it is |
|---|---|---|
| `Demo__MaxVisitors` | `10` | Visitors at once. The next one gets "The demo is busy right now". |
| `Demo__IdleTimeout` | `00:15:00` | Time without a click or a key before the page asks whether the visitor is still there. |
| `Demo__IdleWarning` | `00:02:00` | How long the page counts down before the copy ends. |
| `Demo__MaxLifetime` | `02:00:00` | How long a copy lasts at most. |
| `Demo__MaxBoards` | `20` | Boards per visitor in the simulator. |
| `Demo__TunnelAddress` | `172.30.60.2` (in `compose.yml`) | cloudflared's address on the demo's network: only it may say that the visitor came in over HTTPS. |

With more visitors, give the container more memory too (`mem_limit`, about 250 MB per visitor and 500 MB more).

**If the network clashes:** when `upgrade.sh` can't prepare the demo because the networks overlap, the subnet
`172.30.60.0/28` is in use on the machine. Pick another, e.g. `172.30.61.0/28`, and change the subnet, the
`ip_range`, cloudflared's `ipv4_address` and `Demo__TunnelAddress` in `compose.yml` to match, and grep for the new
subnet in Check it.
