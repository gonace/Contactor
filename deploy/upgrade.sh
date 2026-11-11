#!/usr/bin/env bash
# Upgrades Contactor on this Raspberry Pi: pulls the newest image, checks its signature, brings the app up again
# and waits until it is healthy. The app is down for a few seconds; the relays keep their state on the board. Nothing
# restarts when the image hasn't changed. Safe to run again.
#
#   sudo contactor-upgrade        (install.sh installs it under that name)
#
# Only an image signed with one of our keys is run: when the new image is unsigned or wrongly signed, the app keeps
# running the image it has and the script fails saying why. It asks nothing, so a systemd timer can run it:
# autoupdate-install.sh sets one up. It keeps /opt/contactor/compose.yml as it is; when a release changes it, run
# install.sh again, which rewrites it and then runs this.
set -euo pipefail

readonly DIR=/opt/contactor
readonly IMAGE_PREFIX=ghcr.io/gonace/contactor
# Versions of the app kept on the SD card after an upgrade: the running one and the ones before it, to go back to. Each
# is a few hundred MB, and a new one comes every night there is a release.
readonly KEEP_VERSIONS=3

fail() { echo "Error: $*" >&2; exit 1; }

# cosign checks the image's signature. It runs as a container, so nothing is installed on the Raspberry Pi.
readonly COSIGN=ghcr.io/sigstore/cosign/cosign:v3.1.4@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d
# The public keys our images are signed with, as in deploy/cosign: the main key, and the reserve key whose private
# half is kept offline. An image signed with either is accepted, so switching keys needs no change here. Paste the
# contents of deploy/cosign/contactor-main.pub and contactor-reserve.pub; scripts/images-sign.sh refuses to sign
# with a key that isn't here.
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

# Whether the image with this name, as pulled, is signed with one of COSIGN_KEYS; it says why when it isn't. It checks
# the digest the image was pulled by, with root's Docker login (the image is private).
signed() {
    local image=$1 repository reference key output first="" mount=()
    local config=${DOCKER_CONFIG:-${HOME:-/root}/.docker}/config.json
    repository=${image%@*}
    if [[ "${repository##*/}" == *:* ]]; then
        repository=${repository%:*}
    fi
    reference=$(docker image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$image" 2> /dev/null \
        | grep "^$repository@sha256:" | head -n 1 || true)
    if [[ -z "$reference" ]]; then
        echo "$image wasn't pulled from $repository, so it has no digest to check." >&2
        return 1
    fi
    if ! docker pull --quiet "$COSIGN" > /dev/null; then
        echo "could not pull cosign ($COSIGN)." >&2
        return 1
    fi
    # As root in the container, so it can read the login, which only root may read.
    if [[ -f "$config" ]]; then
        mount=(--volume "$config:/docker/config.json:ro" --env DOCKER_CONFIG=/docker)
    fi
    for key in "${COSIGN_KEYS[@]}"; do
        if output=$(docker run --rm --user 0:0 "${mount[@]}" --env COSIGN_KEY="$key" "$COSIGN" \
            verify --key env://COSIGN_KEY --insecure-ignore-tlog=true "$reference" 2>&1); then
            echo "$reference is signed with our key."
            return 0
        fi
        first=${first:-$output}
    done
    echo "$reference isn't signed with our key. cosign says:" >&2
    grep -v -e 'Skipping tlog verification' -e '^$' <<< "$first" >&2 || true
    return 1
}

[[ $EUID -eq 0 ]] || fail "run it as root, e.g. with sudo."
[[ ${#COSIGN_KEYS[@]} -gt 0 ]] || fail "this script has no signing keys yet; see deploy/cosign/README.md."
# One upgrade at a time, shared with install.sh: another run could pull a new image under the name between this run's
# signature check and the start, so this one would start an image it never checked. A second run waits for the first,
# for up to 10 minutes.
exec 9> /run/contactor-upgrade.lock
flock --wait 600 9 || fail "another install or upgrade has run for 10 minutes; try again once it's done."
[[ -f "$DIR/compose.yml" && -f "$DIR/.env" ]] || fail "Contactor isn't installed in $DIR; run install.sh first."
cd "$DIR"

image=$(docker compose config --images contactor) || fail "could not read $DIR/compose.yml."
[[ "$image" == "$IMAGE_PREFIX"* ]] || fail "expected $IMAGE_PREFIX in compose.yml, found: ${image:-none}."
# The image the app's container runs, by the name it was started from: it was checked when it was started. None yet
# on a first install.
running=""
container=$(docker compose ps --all --quiet contactor 2> /dev/null || true)
if [[ -n "$container" ]]; then
    running=$(docker inspect -f '{{.Image}}' "$container" 2> /dev/null || true)
fi
before=$(docker image inspect -f '{{.Id}}' "$image" 2> /dev/null || true)

# Puts the name back on the image the app runs, so no docker compose up starts an unchecked or refused image by
# accident: on a refusal, and when the run stops between the pull and a passed check. With no image to put back, the
# name comes off the new one.
# shellcheck disable=SC2317 # Called by the trap.
restore() {
    local previous=${running:-$before}
    if [[ -z "$previous" ]] || ! docker tag "$previous" "$image" 2> /dev/null; then
        docker image rm "$image" > /dev/null 2>&1 || true
    fi
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

echo "Pulling the newest images"
docker compose pull --quiet || fail "could not pull the images."

# Any image other than the one the app runs is checked, even one this machine had before the pull: a run that
# stopped halfway may have left an unchecked image under the name.
if [[ "$(docker image inspect -f '{{.Id}}' "$image")" != "$running" ]]; then
    echo "Checking the signature of $image"
    signed "$image" || fail "not upgrading: $image isn't signed with our key. The app keeps running its current image."
fi
# Checked, so the name stays on the new image.
trap - EXIT INT TERM

# The release an image is, from the label the SDK sets from its version, e.g. 2026.11.4.
version_of() { docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2> /dev/null || true; }

echo "Starting the app"
started_before=$(docker compose ps --format '{{.CreatedAt}}' contactor 2> /dev/null || true)
docker compose up -d --remove-orphans || fail "the app was not started; see: cd $DIR && docker compose logs contactor"
if [[ "$(docker compose ps --format '{{.CreatedAt}}' contactor 2> /dev/null || true)" == "$started_before" ]]; then
    echo "The app already runs the newest image ($(version_of "$image"))."
    exit 0
fi

# Removes all but the newest KEEP_VERSIONS of the app's images, never the one the app runs now. Only after a healthy
# start, so a failed upgrade leaves everything as it was. Images still used by a container, or by another name, are
# skipped by docker itself.
remove_old_versions() {
    local id kept=0 current
    current=$(docker inspect -f '{{.Image}}' "$container" 2> /dev/null || true)
    # Newest first; an old version keeps the repository's name (tagged <none>) after "latest" moved on.
    while read -r id; do
        if [[ "$id" == "$current" ]] || (( kept < KEEP_VERSIONS )); then
            kept=$((kept + 1))
            continue
        fi
        if docker image rm "$id" > /dev/null 2>&1; then
            echo "Removed an old version ($id)."
        fi
    done < <(docker image ls --no-trunc --format '{{.ID}}' "$IMAGE_PREFIX" | awk '!seen[$0]++')
}

container=$(docker compose ps --quiet contactor)
for _ in $(seq 1 60); do
    case "$(docker inspect -f '{{.State.Health.Status}}' "$container" 2> /dev/null || true)" in
        healthy)
            echo "Contactor $(version_of "$image") is upgraded and running."
            remove_old_versions
            exit 0
            ;;
        unhealthy)
            break
            ;;
    esac
    sleep 2
done
fail "the app did not become healthy; see: cd $DIR && docker compose logs contactor"
