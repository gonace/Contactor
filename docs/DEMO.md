# Hosting the demo

The public demo gives each visitor their own copy of the app and the simulator, side by side. It runs on one machine
with Docker Compose: two containers, reached through a route on a Cloudflare tunnel you already have, whose connector
(cloudflared) runs on another machine on your LAN. No port is opened on the router.

```
Visitor ─▶ Cloudflare ─▶ your tunnel's connector (LAN) ─▶ <machine>:8080 ─▶ forwarder ─▶ demo:8080
                                                                                (network "demo": internal, no route out)
```

- **demo** (`ghcr.io/gonace/contactor-demo`): Contactor.Demo, with the app and the simulator inside. It is only on
  the network `demo`, which has no route out, and on which the machine itself has no address. A visitor can type any
  board address in their app, and nothing but the forwarder can be reached from the container.
- **forwarder** (Caddy's own image): publishes port 8080 on the machine and only forwards it to the demo, with the
  tunnel's `X-Forwarded-Proto`. Its settings are in `deploy/demo/Caddyfile`.

Port 8080 is open on the machine's LAN address for the tunnel's connector only. Never forward it from the router.
Docker's published ports pass by the machine's own firewall, so the Proxmox firewall lets only the connector in (see
The firewall, in Install).

There is no sign-in in front: the demo is public.

## What you need

- A virtual machine on Proxmox (amd64): Debian 13 or Ubuntu 24.04, 2 CPU cores, 4 GB of memory and 20 GB of disk. The
  demo takes about 250 MB per visitor, and at most 10 visitors at once. Its container may use 3 GB in all.
- Docker Engine 28 or later with the Compose plugin, installed from Docker's own apt repository
  (https://docs.docker.com/engine/install/debian/ or .../ubuntu/), not the distribution's `docker.io` package, which is
  older (Debian 13 has 26.1). Docker 28 is the first that keeps the machine off the demo's network: an older one would
  let a visitor reach the machine's own services, such as SSH.
- A Cloudflare tunnel you already run, with its connector on a machine on the same LAN, and a domain on Cloudflare.
- A classic GitHub token with only `read:packages`: the image is private, as the app's is.

## 1. The route on your tunnel

1. In Cloudflare Zero Trust: **Networks → Tunnels**, open your tunnel, **Public hostname → Add a public hostname**.
2. Hostname: your demo's hostname, e.g. `demo.example.com`.
3. Service: type **HTTP**, URL **`<machine's LAN address>:8080`**, e.g. `192.168.1.40:8080`. Give the machine a fixed
   address, so the route keeps working.
4. Don't add an Access application: the demo is public.
5. In the domain's settings, **SSL/TLS → Edge Certificates → Always Use HTTPS**: on. Then every visitor comes in
   over HTTPS, and the demo marks their cookie Secure.

## 2. Install

On your computer, from the repository:

```sh
scp deploy/demo/compose.yml deploy/demo/Caddyfile <host>:
```

On the machine:

```sh
sudo mkdir -p /opt/contactor-demo
sudo install -m 644 compose.yml Caddyfile /opt/contactor-demo/
sudo docker login ghcr.io -u token
```

`docker login` asks for the GitHub token. No other secret is kept on the machine.

Start it:

```sh
cd /opt/contactor-demo && sudo docker compose up -d
```

The forwarder starts once the demo is healthy, after a few seconds.

**Check it** (once, after the first start; an upgrade doesn't change the network):

1. `cd /opt/contactor-demo && sudo docker compose ps`: both containers say `healthy`.
2. `ip -br addr | grep 172.30.60.` prints nothing: the machine has no IPv4 address on the demo's network (with another
   subnet, grep for that).
3. `ip -6 -o addr show dev br-$(sudo docker network inspect -f '{{.Id}}' contactor-demo_demo | cut -c1-12)` prints
   nothing: the machine has no IPv6 address there either.

   If step 2 or 3 prints a line, stop the demo at once (`sudo docker compose down`): a visitor could reach the machine.
   Upgrade Docker as in What you need, keep `enable_ipv6: false` in `compose.yml`, start it again and check again.
4. Open your hostname. The page shows the simulator and the app, with "Skip setup: start from a template".
5. Choose **Workshop**. The app shows the relays, and switching one shows in the simulator too.

**The firewall:** in Proxmox, select the VM → **Firewall → Add**: direction **in**, action **ACCEPT**, protocol
**tcp**, source the tunnel's connector's address, destination port **8080**. Add one more for SSH (port 22) from where
you log in. Then **Firewall → Options → Firewall: Yes** and **Input Policy: DROP**. The VM's network device needs
**Firewall** ticked too (Hardware → Network Device), and a VM's rules only work when the firewall is on for the
datacenter too (**Datacenter → Firewall → Options → Firewall: Yes**). If you turn that on now, first check the
datacenter's own rules and input policy, so you keep access to Proxmox's web interface and SSH. Check that your hostname still opens, and that port 8080 doesn't
answer from another machine (`curl -m 5 http://<machine's LAN address>:8080` from there times out).

Logs: `sudo docker compose logs -f demo` and `sudo docker compose logs -f forwarder`.

## 3. Upgrade

Each release publishes the demo's image with the app's, with the same version.

```sh
cd /opt/contactor-demo && sudo docker compose pull && sudo docker compose up -d --remove-orphans
```

The visitors' copies end when the demo starts again. When a release changes `deploy/demo/compose.yml` or
`deploy/demo/Caddyfile`, copy both over again first, as in Install, and do Check it once more.

To stay on one version, e.g. after a bad release, add `DEMO_IMAGE=ghcr.io/gonace/contactor-demo:2026.11.15` to
`/opt/contactor-demo/.env` (make the file if there is none) and run `sudo docker compose up -d`. Remove the line to
follow the newest again.

## Settings

The demo's limits are settings, under `environment:` of the `demo` service in `compose.yml`, e.g.
`Demo__MaxVisitors: 5`. Run `sudo docker compose up -d` after a change.

| Setting | Default | What it is |
|---|---|---|
| `Demo__MaxVisitors` | `10` | Visitors at once. The next one gets "The demo is busy right now". |
| `Demo__IdleTimeout` | `00:15:00` | Time without a click or a key before the page asks whether the visitor is still there. |
| `Demo__IdleWarning` | `00:02:00` | How long the page counts down before the copy ends. |
| `Demo__MaxLifetime` | `02:00:00` | How long a copy lasts at most. |
| `Demo__MaxBoards` | `20` | Boards per visitor in the simulator. |
| `Demo__TunnelAddress` | `172.30.60.2` (in `compose.yml`) | The forwarder's address on the demo's network: only it may say that the visitor came in over HTTPS. |

With more visitors, give the container more memory too (`mem_limit`, about 250 MB per visitor and 500 MB more).

**If the network clashes:** when `docker compose up` can't create the demo's network because the networks overlap, the subnet
`172.30.60.0/28` is in use on the machine. Pick another, e.g. `172.30.61.0/28`, and change the subnet, the
`ip_range`, the demo's and the forwarder's `ipv4_address` and `Demo__TunnelAddress` in `compose.yml`, and the demo's
address in `Caddyfile`, to match, and grep for the new subnet in Check it.
