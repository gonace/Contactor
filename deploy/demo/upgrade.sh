#!/usr/bin/env bash
# Starts or upgrades the public demo (docs/DEMO.md): pulls the newest image, checks its signature, and starts exactly
# the image it checked. The visitors' copies end when the demo starts again. Safe to run again.
#
#   sudo bash /opt/contactor-demo/upgrade.sh
#
# It refuses, and leaves the demo as it was, on a Docker Engine older than 28 and on an image that isn't signed with
# one of our keys. After the start it checks that this machine has no address on the demo's network, and stops the
# demo if it has: a visitor could otherwise reach this machine's services (e.g. SSH) as a board.
set -euo pipefail

readonly DIR=/opt/contactor-demo
readonly IMAGE_PREFIX=ghcr.io/gonace/contactor-demo
# The first Docker Engine that keeps this machine off the demo's network (gateway_mode_ipv4: isolated in compose.yml).
# An older one ignores the setting, and the machine keeps the network's first address.
readonly MIN_DOCKER=28

# As in deploy/upgrade.sh: cosign runs as a container, and the public keys are deploy/cosign/contactor-main.pub and
# contactor-reserve.pub. scripts/images-sign.sh refuses to sign with a key that isn't here.
readonly COSIGN=ghcr.io/sigstore/cosign/cosign:v3.1.4@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d
readonly COSIGN_KEYS=(
"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPRbttsnxj+3I6BPt+aRzSluhz9Un
gXBxyvK5NYdVG/ywtSyKmrV6fZ90OM0CJb4aTugO0p6QU7fyh3weYfyYjg==
-----END PUBLIC KEY-----"
"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/L6B3FW9vgpVpMZ9WkkUZYoriYJQ
58dJAHz8BXwrkuF99WS2Pv10Kx8L4IPTIYZbrEcIom6DAkeqWi6kZMqpMQ==
-----END PUBLIC KEY-----"
)

fail() { echo "Error: $*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || fail "run it as root, e.g. with sudo."
command -v ip > /dev/null || fail "ip (iproute2) is missing; it checks the demo's network. Install it: apt install iproute2."
[[ -f "$DIR/compose.yml" && -f "$DIR/.env" ]] || fail "the demo isn't installed in $DIR; see docs/DEMO.md."
cd "$DIR"

engine=$(docker version --format '{{.Server.Version}}') || fail "could not reach Docker."
major=${engine%%.*}
if ! [[ "$major" =~ ^[0-9]+$ ]] || (( major < MIN_DOCKER )); then
    fail "Docker $engine is too old: the demo needs Docker $MIN_DOCKER or later, from Docker's own apt repository (docs/DEMO.md). Nothing was changed."
fi

image=$(docker compose config --images demo) || fail "could not read $DIR/compose.yml."
[[ "$image" == "$IMAGE_PREFIX"* ]] || fail "expected $IMAGE_PREFIX in compose.yml, found: ${image:-none}."

echo "Pulling $image"
docker compose pull --quiet || fail "could not pull the images."
repository=${image%@*}
if [[ "${repository##*/}" == *:* ]]; then
    repository=${repository%:*}
fi
reference=$(docker image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$image" 2> /dev/null \
    | grep "^$repository@sha256:" | head -n 1 || true)
[[ -n "$reference" ]] || fail "$image wasn't pulled from $repository, so it has no digest to check."

echo "Checking the signature of $reference"
docker pull --quiet "$COSIGN" > /dev/null || fail "could not pull cosign ($COSIGN)."
# As root in the container, so it can read root's Docker login (the image is private).
config=${DOCKER_CONFIG:-${HOME:-/root}/.docker}/config.json
mount=()
if [[ -f "$config" ]]; then
    mount=(--volume "$config:/docker/config.json:ro" --env DOCKER_CONFIG=/docker)
fi
signed="" first=""
for key in "${COSIGN_KEYS[@]}"; do
    if output=$(docker run --rm --user 0:0 "${mount[@]}" --env COSIGN_KEY="$key" "$COSIGN" \
        verify --key env://COSIGN_KEY --insecure-ignore-tlog=true "$reference" 2>&1); then
        signed=yes
        break
    fi
    first=${first:-$output}
done
if [[ -z "$signed" ]]; then
    echo "cosign says:" >&2
    grep -v -e 'Skipping tlog verification' -e '^$' <<< "$first" >&2 || true
    # The name comes off the refused image, so a plain docker compose up pulls again instead of starting it.
    docker image rm "$image" > /dev/null 2>&1 || true
    fail "not starting: $reference isn't signed with our key. The demo keeps running its current image."
fi

# By the digest just checked, so a tag moved in the meantime can't start anything else.
echo "Starting the demo"
DEMO_IMAGE=$reference docker compose up -d --remove-orphans || fail "the demo was not started; see: cd $DIR && docker compose logs"

# The bridge of the demo's network must have no IPv4 address on this machine.
network=$(docker network ls --quiet --no-trunc \
    --filter label=com.docker.compose.project=contactor-demo --filter label=com.docker.compose.network=demo)
[[ -n "$network" ]] || { docker compose down; fail "could not find the demo's network; the demo is stopped."; }
bridge=$(docker network inspect -f '{{index .Options "com.docker.network.bridge.name"}}' "$network")
bridge=${bridge:-br-${network::12}}
if [[ -n "$(ip -4 -o addr show dev "$bridge" 2>&1)" ]]; then
    docker compose down
    fail "this machine has an address on the demo's network ($bridge), so a visitor could reach it; the demo is stopped. Use Docker $MIN_DOCKER or later and keep gateway_mode_ipv4: isolated in compose.yml."
fi

docker image prune -f > /dev/null
echo "The demo runs $reference."
