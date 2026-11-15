#!/usr/bin/env bash
# Tests the update helper (install/contactor-updater.sh) for real, without a Raspberry Pi: with Docker on this machine,
# a registry of its own on localhost:5000, a signing key made for the test, and small stand-in images of the app (a
# shell script that answers the health check and update-backup as the app does, and writes into its "database" which
# version ran). It runs the helper as its service would and checks what it did:
#
#   a good update; an update to a version that never gets healthy (it goes back, with the database as it was); an
#   unsigned image (refused, nothing changed); a power cut just before the restart and one during the health check
#   (the next run finishes the update or goes back, after a new start of the machine); a cancel during the download;
#   requests that must do nothing (left behind, unknown, a symlink, from before the machine started, already up to
#   date); files in the app's folder that must not hang the helper (a FIFO, a link to /dev/urandom, a folder it can't
#   remove); and install.sh refusing to run while the helper runs.
#
#   sudo bash scripts/updater-test.sh
#
# It needs root, Docker with Compose, and setpriv. COSIGN is the cosign image to sign and check with (by default the
# one the helper uses). It removes what it made when it ends; the stand-in images stay in Docker's cache.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
readonly ROOT
readonly HELPER=$ROOT/install/contactor-updater.sh
readonly COSIGN=${COSIGN:-ghcr.io/sigstore/cosign/cosign:v3.1.4@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d}
readonly REGISTRY=localhost:5000
readonly REPOSITORY=$REGISTRY/contactor-updater-test
readonly REGISTRY_CONTAINER=contactor-updater-test-registry

[[ $EUID -eq 0 ]] || { echo "Run it as root (sudo)." >&2; exit 1; }
WORK=$(mktemp -d)
chmod 755 "$WORK"
readonly WORK
readonly DIR=$WORK/opt/contactor
readonly STATE=$WORK/state
FAILED=0
# The machine's start, as the helper sees it: a power cut starts it again.
BOOT=test-start-1

cleanup() {
    docker compose --project-directory "$DIR" -f "$DIR/compose.yml" down --timeout 1 > /dev/null 2>&1 || true
    docker rm -f "$REGISTRY_CONTAINER" > /dev/null 2>&1 || true
    rm -rf -- "$WORK"
}
trap cleanup EXIT

say() { printf '\n== %s\n' "$*"; }
pass() { printf '  ok   %s\n' "$*"; }
fail() { printf '  FAIL %s\n' "$*"; FAILED=$((FAILED + 1)); }
check() {
    local what=$1
    shift
    if "$@"; then pass "$what"; else fail "$what"; fi
}

status_of() { sed -n "s/^$1=//p" "$DIR/data/updates/status" 2> /dev/null || true; }
running_image() { docker inspect -f '{{.Image}}' contactor 2> /dev/null || true; }
image_id() { docker image inspect -f '{{.Id}}' "$1" 2> /dev/null || true; }
as_app() { setpriv --reuid=1654 --regid=1654 --clear-groups -- "$@"; }
# shellcheck disable=SC2016 # expanded by the inner shell
request() { printf '%s\n' "$1" | as_app sh -c 'cat > "$1"' sh "$DIR/data/updates/request"; }

helper() {
    env CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" \
        CONTACTOR_COSIGN_KEYS="$(cat "$WORK/keys/cosign.pub")" CONTACTOR_UPDATER_STATE="$STATE" \
        CONTACTOR_HEALTH_SECONDS="${HEALTH_SECONDS:-60}" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1 \
        "$@" bash "$HELPER" >> "$WORK/helper.log" 2>&1
}

# A stand-in of the app's image: version $1, "good" or "bad" (never healthy), "big" for a good one that takes a while
# to download (600 MB that don't compress).
build() {
    local version=$1 mode=$2 tag=$3
    mkdir -p "$WORK/image"
    cat > "$WORK/image/dotnet" <<'EOF'
#!/bin/sh
# Stands in for "dotnet /app/Contactor.Web.dll [command]".
case "${2:-}" in
    healthcheck)
        [ "$MODE" = good ] && [ -f /tmp/started ]
        exit $?
        ;;
    update-backup)
        # A group starting or running on, as the app's update-backup says it (UpdateBackup.GroupsBusyExitCode).
        [ ! -f /data/groups-busy ] || exit 3
        mkdir -p /data/updates && cp /data/contactor.db /data/updates/before-update.db.partial \
            && mv /data/updates/before-update.db.partial /data/updates/before-update.db
        exit $?
        ;;
esac
echo "$VERSION was here" >> /data/contactor.db
# What the update's log must never show.
echo "starting with Tunnel__Token=eyJhIjoiMTIzNDU2Nzg5MCJ9.c2VjcmV0 and tskey-auth-kQ1w2E3r4T5y6U7i8O9p"
touch /tmp/started
trap 'exit 0' TERM
while :; do sleep 1; done
EOF
    chmod 755 "$WORK/image/dotnet"
    cat > "$WORK/image/Dockerfile" <<'EOF'
FROM busybox:1.36
ARG VERSION
ARG MODE
LABEL org.opencontainers.image.version=$VERSION
ENV VERSION=$VERSION MODE=$MODE
COPY dotnet big /usr/local/bin/
USER 1654:1654
ENTRYPOINT ["dotnet", "/app/Contactor.Web.dll"]
EOF
    if [[ "$mode" == big ]]; then
        head -c 600M /dev/urandom > "$WORK/image/big"
        mode=good
    else
        : > "$WORK/image/big"
    fi
    docker build --quiet --build-arg VERSION="$version" --build-arg MODE="$mode" -t "$REPOSITORY:$tag" "$WORK/image" > /dev/null
    docker push --quiet "$REPOSITORY:$tag" > /dev/null
    docker image rm "$REPOSITORY:$tag" > /dev/null
}

# Pushes stand-in $1 as latest in the registry, and signs it unless $2 is "unsigned". Prints its reference by
# digest. The machine keeps only what it had: the helper downloads the new version itself.
release() {
    local tag=$1 signed=${2:-signed} digest id running
    running=$(running_image)
    docker pull --quiet "$REPOSITORY:$tag" > /dev/null
    id=$(image_id "$REPOSITORY:$tag")
    docker tag "$REPOSITORY:$tag" "$REPOSITORY:latest"
    docker push --quiet "$REPOSITORY:latest" > /dev/null
    digest=$(docker image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$id" | grep "^$REPOSITORY@" | head -n 1)
    docker image rm "$REPOSITORY:$tag" > /dev/null
    if [[ -n "$running" ]]; then
        docker tag "$running" "$REPOSITORY:latest"
    else
        docker image rm "$REPOSITORY:latest" > /dev/null
    fi
    if [[ "$id" != "$running" ]]; then
        docker image rm "$id" > /dev/null 2>&1 || true
    fi
    if [[ "$signed" == signed ]]; then
        docker run --rm --network host --user 0 -v "$WORK/keys:/keys" -v "$ROOT/deploy/cosign:/config:ro" \
            -e COSIGN_PASSWORD= "$COSIGN" sign --yes --key /keys/cosign.key \
            --signing-config /config/signing-config.json "$digest" > "$WORK/sign.log" 2>&1 \
            || { cat "$WORK/sign.log" >&2; exit 1; }
    fi
    printf '%s' "$digest"
}

say "Setting up: a registry, a key, the stand-in images and an installation in $DIR"
docker run -d --name "$REGISTRY_CONTAINER" -p 127.0.0.1:5000:5000 registry:2 > /dev/null
mkdir -p "$WORK/keys"
docker run --rm --user 0 -v "$WORK/keys:/keys" -w /keys -e COSIGN_PASSWORD= "$COSIGN" generate-key-pair > /dev/null 2>&1
for version in 2026.11.14:good:v1 2026.11.20:good:v2 2026.11.21:bad:v3 2026.11.22:good:v4 2026.11.23:good:v5 2026.11.24:bad:v6 2026.11.25:big:v7 2026.11.26:good:v8 2026.11.27:good:v9; do
    IFS=: read -r number mode tag <<< "$version"
    build "$number" "$mode" "$tag"
done
V1=$(release v1)
mkdir -p "$DIR/data"
cp "$ROOT/install/compose.yml" "$DIR/compose.yml"
chown 1654:1654 "$DIR/data"
chmod 750 "$DIR/data"
docker pull --quiet "$V1" > /dev/null
docker tag "$V1" "$REPOSITORY:latest"
CONTACTOR_IMAGE=$V1 docker compose --project-directory "$DIR" -f "$DIR/compose.yml" up -d --pull never > /dev/null 2>&1
for _ in $(seq 1 30); do
    [[ "$(docker inspect -f '{{.State.Health.Status}}' contactor)" == healthy ]] && break
    sleep 2
done
# The first run makes the updates folder and says the helper is there.
helper
check "the helper says it is installed" grep -qx 'version=1' "$DIR/data/updates/helper"

say "A request when there is nothing newer"
request update
helper
check "it says the app is up to date" test "$(status_of state)" = current
check "the app runs 2026.11.14 still" test "$(running_image)" = "$(image_id "$V1")"

say "A good update: 2026.11.14 to 2026.11.20"
V2=$(release v2)
request update
helper
check "it says done" test "$(status_of state)" = "done"
check "from 2026.11.14 to 2026.11.20" test "$(status_of from)-$(status_of to)" = 2026.11.14-2026.11.20
check "the app runs 2026.11.20 by its digest" test "$(running_image)" = "$(image_id "$V2")"
check "latest names 2026.11.20" test "$(image_id "$REPOSITORY:latest")" = "$(image_id "$V2")"
check "the database was backed up first" grep -qx '2026.11.14 was here' "$DIR/data/updates/before-update.db"
check "the request is gone" test ! -e "$DIR/data/updates/request"
check "there is no journal left" test ! -e "$STATE/journal"
log_file=$(find "$DIR/data/updates/logs" -name "update-*.log" | sort | tail -n 1)
check "the log says what happened" grep -q 'is up and healthy' "$log_file"
check "files in the data folder stay the app's" test -z "$(find "$DIR/data" ! -user 1654)"

say "An update to a version that never gets healthy: 2026.11.21 goes back to 2026.11.20"
V3=$(release v3)
cp "$DIR/data/contactor.db" "$WORK/db-before"
request update
HEALTH_SECONDS=20 helper
check "it says it went back" test "$(status_of state)" = went-back
check "the reason is the health check" test "$(status_of reason)" = health
check "the app runs 2026.11.20 again, by its digest" test "$(running_image)" = "$(image_id "$V2")"
check "latest names 2026.11.20 again" test "$(image_id "$REPOSITORY:latest")" = "$(image_id "$V2")"
check "2026.11.21 is removed" test -z "$(image_id "$V3")"
check "the database is as it was before the update (2026.11.20 has run on it again since)" \
    test "$(grep -vx '2026.11.20 was here' "$DIR/data/contactor.db")" = "$(grep -vx '2026.11.20 was here' "$WORK/db-before")"
check "2026.11.21's writes are gone" bash -c "! grep -q '2026.11.21' '$DIR/data/contactor.db'"
log_file=$(find "$DIR/data/updates/logs" -name "update-*.log" | sort | tail -n 1)
check "the log has 2026.11.21's own log" grep -q 'starting with Tunnel__Token' "$log_file"
check "with its secrets stripped" bash -c "! grep -q -e 'eyJhIjoi' -e 'tskey-auth' '$log_file'"

say "An unsigned image: refused, nothing changed"
V4_UNSIGNED=$(release v4 unsigned)
before=$(docker inspect -f '{{.Id}}' contactor)
request update
helper
check "it says it failed" test "$(status_of state)" = failed
check "because of the signature" test "$(status_of reason)" = signature
check "the same container runs on" test "$(docker inspect -f '{{.Id}}' contactor)" = "$before"
check "the unsigned image is removed" test -z "$(image_id "$V4_UNSIGNED")"
check "latest names 2026.11.20 still" test "$(image_id "$REPOSITORY:latest")" = "$(image_id "$V2")"

say "A power cut just before the restart: the next start finishes the update"
V5=$(release v5)
request update
helper CONTACTOR_TEST_STOP_AT=restart || true
check "the journal says the restart was next" grep -qx 'phase=restart' "$STATE/journal"
check "the app still runs 2026.11.20" test "$(running_image)" = "$(image_id "$V2")"
BOOT=test-start-2
helper
check "after the next start it says done" test "$(status_of state)" = "done"
check "the app runs 2026.11.23" test "$(running_image)" = "$(image_id "$V5")"
check "there is no journal left" test ! -e "$STATE/journal"
check "the older versions are removed, 2026.11.20 kept to go back to" \
    test "$(docker image ls -q "$REPOSITORY" | sort -u | wc -l)" = 2

say "A power cut while the new version is checked, and it never gets healthy: the next start goes back"
V6=$(release v6)
request update
helper CONTACTOR_TEST_STOP_AT=check || true
check "the journal says it was being checked" grep -qx 'phase=check' "$STATE/journal"
check "2026.11.24 runs" test "$(running_image)" = "$(image_id "$V6")"
BOOT=test-start-3
HEALTH_SECONDS=20 helper
check "after the next start it says it went back" test "$(status_of state)" = went-back
check "the app runs 2026.11.23 again" test "$(running_image)" = "$(image_id "$V5")"
check "2026.11.24's writes are gone" bash -c "! grep -q '2026.11.24' '$DIR/data/contactor.db'"

say "Requests that do nothing"
changed=$(status_of changed)
before=$(docker inspect -f '{{.Id}}' contactor)
request update
touch -d '10 minutes ago' "$DIR/data/updates/request"
helper
check "a request left behind is ignored, and removed" test "$(status_of changed)" = "$changed" -a ! -e "$DIR/data/updates/request"
request 'update --to evil:latest'
helper
check "an unknown request is ignored" test "$(status_of changed)" = "$changed"
as_app ln -s /etc/shadow "$DIR/data/updates/request"
cp /etc/shadow "$WORK/shadow"
helper
check "a symlink to a root-only file is ignored" test "$(status_of changed)" = "$changed" -a ! -L "$DIR/data/updates/request"
check "and that file is untouched" cmp -s /etc/shadow "$WORK/shadow"
as_app rm -f "$DIR/data/updates/status"
as_app ln -s /etc/shadow "$DIR/data/updates/status"
release v5 > /dev/null
request update
helper
check "a status file made a symlink to a root-only file isn't written through" cmp -s /etc/shadow "$WORK/shadow"
check "the same container runs on all along" test "$(docker inspect -f '{{.Id}}' contactor)" = "$before"

say "A cancel during the download: nothing changed, and \"latest\" stays on the running version"
running=$(running_image)
V7=$(release v7)
request update
helper &
helper_run=$!
for _ in $(seq 1 60); do
    [[ "$(status_of step)" == download && "$(status_of state)" == running ]] && break
    sleep 0.5
done
sleep 2
check "the download was still under way when cancel came" pgrep -f "^docker pull --quiet $REPOSITORY:latest" > /dev/null
request cancel
wait "$helper_run" || true
check "it says the update was cancelled" test "$(status_of state)-$(status_of reason)" = failed-cancelled
check "no docker pull is left running" bash -c "! pgrep -f '^docker pull --quiet $REPOSITORY:latest' > /dev/null"
sleep 10
check "latest still names the running version, 10 s later" test "$(image_id "$REPOSITORY:latest")" = "$running"
check "the same version runs" test "$(running_image)" = "$running"
printf '       (2026.11.25 %s on this machine after the cancel)\n' "$([[ -n "$(image_id "$V7")" ]] && echo is || echo isn\'t)"

say "A group switching just before the restart: it waits, then goes on"
V8=$(release v8)
as_app touch "$DIR/data/groups-busy"
request update
helper &
helper_run=$!
for _ in $(seq 1 60); do
    [[ "$(status_of step)" == waiting ]] && break
    sleep 0.5
done
check "it says it waits for the relays" test "$(status_of state)-$(status_of step)" = running-waiting
check "the same version runs meanwhile" test "$(running_image)" = "$running"
as_app rm -f "$DIR/data/groups-busy"
wait "$helper_run" || true
check "once the group is done it updates" test "$(status_of state)-$(status_of to)" = done-2026.11.26
check "the app runs 2026.11.26" test "$(running_image)" = "$(image_id "$V8")"

say "A group still switching after the wait: the update stops, nothing changed"
running=$(running_image)
V9=$(release v9)
as_app touch "$DIR/data/groups-busy"
request update
helper CONTACTOR_WAIT_SECONDS=6
check "it says why it stopped" test "$(status_of state)-$(status_of reason)" = failed-switching
check "the same version runs" test "$(running_image)" = "$running"
check "latest names the running version" test "$(image_id "$REPOSITORY:latest")" = "$running"
check "2026.11.27 is removed" test -z "$(image_id "$V9")"
as_app rm -f "$DIR/data/groups-busy"

say "Requests from before the machine started: ignored"
changed=$(status_of changed)
request update
BOOT=test-start-4
helper
check "the first run after a start takes no request" test "$(status_of changed)" = "$changed" -a ! -e "$DIR/data/updates/request"

say "Files in the app's folder that must not hang the helper"
as_app mkfifo "$DIR/data/updates/request"
check "a FIFO as the request: the helper ends, and the FIFO is gone" \
    bash -c "timeout 60 env $(printf '%q ' CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" CONTACTOR_COSIGN_KEYS="$(cat "$WORK/keys/cosign.pub")" CONTACTOR_UPDATER_STATE="$STATE" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1) bash $HELPER >> $WORK/helper.log 2>&1 && test ! -e $DIR/data/updates/request"
as_app rm -f "$DIR/data/updates/helper"
as_app ln -s /dev/urandom "$DIR/data/updates/helper"
check "the helper file a link to /dev/urandom: the helper ends" \
    bash -c "timeout 60 env $(printf '%q ' CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" CONTACTOR_COSIGN_KEYS="$(cat "$WORK/keys/cosign.pub")" CONTACTOR_UPDATER_STATE="$STATE" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1) bash $HELPER >> $WORK/helper.log 2>&1"
check "and it is a plain file again" test -f "$DIR/data/updates/helper" -a ! -L "$DIR/data/updates/helper"
as_app mkdir -p "$DIR/data/updates/request/inside"
as_app touch "$DIR/data/updates/request/inside/file"
helper
check "a folder as the request is removed" test ! -e "$DIR/data/updates/request"
as_app mkdir -p "$DIR/data/updates/request/inside"
as_app touch "$DIR/data/updates/request/inside/file"
as_app chmod 500 "$DIR/data/updates/request/inside"
helper
check "a request it can't remove: it waits, then ends" grep -q "Couldn't remove the request; waiting 1 seconds" "$WORK/helper.log"
as_app chmod 700 "$DIR/data/updates/request/inside"
as_app rm -rf "$DIR/data/updates/request"

say "install.sh while the helper runs: it refuses, and changes nothing"
flock "$STATE/lock" sleep 30 &
holder=$!
sleep 1
if CONTACTOR_UPDATER_STATE=$STATE sh "$ROOT/install/install.sh" > "$WORK/install.log" 2>&1; then
    fail "install.sh ran while the helper ran"
else
    check "install.sh says an update is under way" grep -q "is being updated from its Settings right now" "$WORK/install.log"
fi
if CONTACTOR_UPDATER_STATE=$STATE sh "$ROOT/install/install.sh" --uninstall > "$WORK/install.log" 2>&1; then
    fail "install.sh --uninstall ran while the helper ran"
else
    check "install.sh --uninstall says so too" grep -q "is being updated from its Settings right now" "$WORK/install.log"
fi
kill "$holder" 2> /dev/null || true
wait "$holder" 2> /dev/null || true
check "the app runs on all along" test "$(running_image)" = "$running"

printf '\nThe helper said:\n'
if [[ "$FAILED" -gt 0 ]]; then
    sed 's/^/  | /' "$WORK/helper.log"
else
    sed 's/^/  | /' "$WORK/helper.log" | tail -n 40
fi
if [[ "$FAILED" -gt 0 ]]; then
    printf '\n%s check(s) failed.\n' "$FAILED"
    exit 1
fi
printf '\nEvery check passed.\n'
