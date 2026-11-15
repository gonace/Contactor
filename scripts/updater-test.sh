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
#   remove); install.sh refusing to run while the helper runs; the helper bringing itself up to date from the new
#   image (a newer helper is used from the next run on; one that fails its check, the same one, an older one or none
#   changes nothing; nothing in the data folder or the running container changes what is installed); and install.sh
#   taking compose.yml and the helper from the image it installs, and the helper from the newest one when it runs
#   again (nothing downloaded from a release; an image without them, or with a broken helper, says so).
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
# The helper and its units as install.sh puts them on the machine: the helper brings these up to date itself.
readonly INSTALLED=$WORK/sbin/contactor-updater
readonly UNITS=$WORK/units
# The container the helper makes to read a new image's copies of itself.
readonly READER=contactor-updater-test-read
FAILED=0
# The machine's start, as the helper sees it: a power cut starts it again.
BOOT=test-start-1

cleanup() {
    docker compose --project-directory "$DIR" -f "$DIR/compose.yml" down --timeout 1 > /dev/null 2>&1 || true
    # With its volume: the stand-in images pushed to it take about 600 MB.
    docker rm -f -v "$REGISTRY_CONTAINER" > /dev/null 2>&1 || true
    docker rm -f -v "$READER" contactor-install-read > /dev/null 2>&1 || true
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
        CONTACTOR_UPDATER_PATH="$INSTALLED" CONTACTOR_UNITS="$UNITS" CONTACTOR_READER="$READER" PATH="$WORK/bin:$PATH" \
        CONTACTOR_HEALTH_SECONDS="${HEALTH_SECONDS:-60}" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1 \
        "$@" bash "$INSTALLED" >> "$WORK/helper.log" 2>&1
}

# The copies of the helper and its units a stand-in image brings in /app/updater, and compose.yml in /app/install, as
# the Dockerfile puts them there:
# "same" (this one), "newer" and "newer2" (a higher HELPER_VERSION, units with a line more), "broken" (a higher
# version that bash can't read), "older" (a lower version) and "none" (no copies, as releases before them).
variants() {
    local name version
    for name in same newer newer2 broken older; do
        mkdir -p "$WORK/variants/$name"
        cp "$ROOT/install/contactor-updater.sh" "$ROOT/install/contactor-updater.service" "$ROOT/install/contactor-updater.path" \
            "$WORK/variants/$name/"
    done
    for name in newer:901 newer2:903 broken:902 older:0; do
        version=${name#*:}
        name=${name%%:*}
        sed -i "s/^readonly HELPER_VERSION=[0-9]*$/readonly HELPER_VERSION=$version/" "$WORK/variants/$name/contactor-updater.sh"
        printf '# (test) the %s units\n' "$name" >> "$WORK/variants/$name/contactor-updater.service"
        printf '# (test) the %s units\n' "$name" >> "$WORK/variants/$name/contactor-updater.path"
    done
    printf 'if [[ then\n' >> "$WORK/variants/broken/contactor-updater.sh"
}

# A stand-in of the app's image: version $1, "good" or "bad" (never healthy), "big" for a good one that takes a while
# to download (600 MB that don't compress); with the helper copies $4 (see variants).
build() {
    local version=$1 mode=$2 tag=$3 copies=${4:-none}
    rm -rf "$WORK/image"
    mkdir -p "$WORK/image/root/usr/local/bin"
    if [[ "$copies" != none ]]; then
        mkdir -p "$WORK/image/root/app/updater" "$WORK/image/root/app/install"
        cp "$WORK/variants/$copies/"* "$WORK/image/root/app/updater/"
        cp "$ROOT/install/compose.yml" "$WORK/image/root/app/install/"
    fi
    cat > "$WORK/image/root/usr/local/bin/dotnet" <<'EOF'
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
    chmod 755 "$WORK/image/root/usr/local/bin/dotnet"
    cat > "$WORK/image/Dockerfile" <<'EOF'
FROM busybox:1.36
ARG VERSION
ARG MODE
LABEL org.opencontainers.image.version=$VERSION
ENV VERSION=$VERSION MODE=$MODE
COPY root/ /
USER 1654:1654
ENTRYPOINT ["dotnet", "/app/Contactor.Web.dll"]
EOF
    if [[ "$mode" == big ]]; then
        head -c 600M /dev/urandom > "$WORK/image/root/usr/local/bin/big"
        mode=good
    else
        : > "$WORK/image/root/usr/local/bin/big"
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
variants
for version in 2026.11.14:good:v1 2026.11.20:good:v2 2026.11.21:bad:v3 2026.11.22:good:v4 2026.11.23:good:v5 \
    2026.11.24:bad:v6 2026.11.25:big:v7 2026.11.26:good:v8 2026.11.27:good:v9 2026.11.28:good:v10:newer \
    2026.11.29:good:v11:broken 2026.11.30:good:v12:newer 2026.12.1:good:v13 2026.12.2:good:v14:older \
    2026.12.3:good:v15:newer2; do
    IFS=: read -r number mode tag copies <<< "$version"
    build "$number" "$mode" "$tag" "$copies"
done
mkdir -p "$WORK/sbin" "$UNITS" "$WORK/bin"
# The helper never reloads this machine's own systemd: a stand-in notes what it was asked.
printf '#!/bin/sh\necho "$*" >> "%s"\n' "$WORK/systemctl.log" > "$WORK/bin/systemctl"
chmod 755 "$WORK/bin/systemctl"
cp "$HELPER" "$INSTALLED"
cp "$ROOT/install/contactor-updater.service" "$ROOT/install/contactor-updater.path" "$UNITS/"
chmod 755 "$INSTALLED"
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
HELPER_VERSION=$(sed -n 's/^readonly HELPER_VERSION=//p' "$HELPER")
check "the helper says it is installed" grep -qx "version=$HELPER_VERSION" "$DIR/data/updates/helper"

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
# Cancel once the pull of the new version is under way.
for _ in $(seq 1 150); do
    pgrep -f "^docker pull --quiet $REPOSITORY:latest" > /dev/null && break
    sleep 0.2
done
check "the download was under way when cancel came" bash -c "pgrep -f '^docker pull --quiet $REPOSITORY:latest' > /dev/null"
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
log_file=$(find "$DIR/data/updates/logs" -name "update-*.log" | sort | tail -n 1)
check "its log says it waits once, and why it stopped" \
    test "$(grep -c 'Waiting for the relays to finish switching' "$log_file")-$(grep -c 'still switching after' "$log_file")" = 1-1
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
    bash -c "timeout 60 env $(printf '%q ' CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" CONTACTOR_COSIGN_KEYS="$(cat "$WORK/keys/cosign.pub")" CONTACTOR_UPDATER_STATE="$STATE" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1) bash $INSTALLED >> $WORK/helper.log 2>&1 && test ! -e $DIR/data/updates/request"
as_app rm -f "$DIR/data/updates/helper"
as_app ln -s /dev/urandom "$DIR/data/updates/helper"
check "the helper file a link to /dev/urandom: the helper ends" \
    bash -c "timeout 60 env $(printf '%q ' CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" CONTACTOR_COSIGN_KEYS="$(cat "$WORK/keys/cosign.pub")" CONTACTOR_UPDATER_STATE="$STATE" CONTACTOR_BOOT_ID="$BOOT" CONTACTOR_STUCK_SECONDS=1) bash $INSTALLED >> $WORK/helper.log 2>&1"
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
# Held by this script itself, as the helper holds it while it runs: let go of below, whole (a child would keep it).
exec 8> "$STATE/lock"
flock 8
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
exec 8>&-
check "the app runs on all along" test "$(running_image)" = "$running"

say "A first run after a start that ends early still counts: the next \"Update now\" is taken"
mv "$DIR/compose.yml" "$WORK/compose.yml"
BOOT=test-start-5
helper
check "the run without compose.yml noted this start" grep -qx test-start-5 "$STATE/boot"
mv "$WORK/compose.yml" "$DIR/compose.yml"
changed=$(status_of changed)
request update
helper
check "the next request is taken" test "$(status_of changed)" != "$changed"

say "The helper brings itself up to date: a newer helper in 2026.11.28"
release v10 > /dev/null
request update
helper
check "it says done" test "$(status_of state)-$(status_of to)" = done-2026.11.28
check "the run that brought it was the old helper" test "$(status_of helper)" = "$HELPER_VERSION"
check "the new helper is in place" cmp -s "$INSTALLED" "$WORK/variants/newer/contactor-updater.sh"
check "the old one is kept beside it" cmp -s "$INSTALLED.previous" "$HELPER"
check "the service unit is the new one" cmp -s "$UNITS/contactor-updater.service" "$WORK/variants/newer/contactor-updater.service"
check "the path unit is the new one" cmp -s "$UNITS/contactor-updater.path" "$WORK/variants/newer/contactor-updater.path"
check "the old units are kept beside them" cmp -s "$UNITS/contactor-updater.service.previous" "$ROOT/install/contactor-updater.service"
check "no half-written file is left" test -z "$(find "$WORK/sbin" "$UNITS" -name '*.new.*')"
check "the new helper stays executable" test -x "$INSTALLED"
log_file=$(find "$DIR/data/updates/logs" -name "update-*.log" | sort | tail -n 1)
check "the log says so" grep -q "The update helper is brought up to date (version $HELPER_VERSION to 901)" "$log_file"
if [[ -d /run/systemd/system ]]; then
    check "systemd is asked to read the new units (the test's stand-in)" grep -qx daemon-reload "$WORK/systemctl.log"
else
    check "without systemd, systemctl isn't asked" test ! -e "$WORK/systemctl.log"
fi
check "the container that read the image is gone" test -z "$(docker ps -aq --filter "name=^$READER\$")"
helper
check "the next run is the new helper" grep -qx 'version=901' "$DIR/data/updates/helper"

installed_as() { stat -c '%i %Y' "$INSTALLED" "$INSTALLED.previous" "$UNITS/contactor-updater.service" "$UNITS/contactor-updater.path"; }

say "A newer helper that fails its check (2026.11.29): the one in place stays, and the update is done"
before=$(installed_as)
release v11 > /dev/null
request update
helper
check "it says done" test "$(status_of state)-$(status_of to)" = done-2026.11.29
check "nothing is replaced" test "$(installed_as)" = "$before"
log_file=$(find "$DIR/data/updates/logs" -name "update-*.log" | sort | tail -n 1)
check "the log says why" grep -q "didn't pass the check; this one (version 901) stays" "$log_file"

say "The same helper (2026.11.30): nothing is replaced"
release v12 > /dev/null
request update
helper
check "it says done" test "$(status_of state)-$(status_of to)" = done-2026.11.30
check "nothing is replaced" test "$(installed_as)" = "$before"

say "No helper in the image (2026.12.1, as releases before it): nothing is replaced"
release v13 > /dev/null
request update
helper
check "it says done" test "$(status_of state)-$(status_of to)" = done-2026.12.1
check "nothing is replaced" test "$(installed_as)" = "$before"

say "An older helper (2026.12.2): nothing is replaced"
release v14 > /dev/null
request update
helper
check "it says done" test "$(status_of state)-$(status_of to)" = done-2026.12.2
check "nothing is replaced" test "$(installed_as)" = "$before"

say "Nothing in the data folder or the running app changes what is installed (2026.12.3, after a power cut)"
V15=$(release v15)
request update
helper CONTACTOR_TEST_STOP_AT=check || true
check "2026.12.3 runs" test "$(running_image)" = "$(image_id "$V15")"
# What the app, or anyone in its container, could put there.
docker exec -u 0 contactor sh -c 'echo "echo evil" > /app/updater/contactor-updater.sh'
for path in updates/contactor-updater.sh updates/updater/contactor-updater.sh app/updater/contactor-updater.sh; do
    as_app mkdir -p "$(dirname "$DIR/data/$path")"
    # shellcheck disable=SC2016 # expanded by the inner shell
    printf '#!/usr/bin/env bash\nreadonly HELPER_VERSION=999\necho evil\n' | as_app sh -c 'cat > "$1"' sh "$DIR/data/$path"
done
BOOT=test-start-6
helper
check "after the next start it says done" test "$(status_of state)-$(status_of to)" = done-2026.12.3
check "the installed helper is the image's own copy" cmp -s "$INSTALLED" "$WORK/variants/newer2/contactor-updater.sh"
check "the units are the image's own copies" cmp -s "$UNITS/contactor-updater.service" "$WORK/variants/newer2/contactor-updater.service"
check "no container it made to read the image is left" test "$(docker ps -aq --filter "ancestor=$(image_id "$V15")" | wc -l)" = 1

say "update_helper on its own: what the image holds, a missing helper, leftovers"
# Runs only update_helper, as after a healthy update, on image $1 (by digest), against the installation in
# $WORK/direct; says how it ended, so a run that stops halfway shows.
direct() {
    # shellcheck disable=SC2016 # expanded by the inner shell
    env PATH="$WORK/bin:$PATH" CONTACTOR_DIR="$DIR" CONTACTOR_REPOSITORY="$REPOSITORY" CONTACTOR_COSIGN="$COSIGN" \
        TEST_WORK="$WORK" CONTACTOR_COSIGN_KEYS=unused CONTACTOR_UPDATER_STATE="$WORK/direct/state" CONTACTOR_READER="$READER" \
        CONTACTOR_UPDATER_PATH="$WORK/direct/sbin/contactor-updater" CONTACTOR_UNITS="$WORK/direct/units" \
        bash -c 'source <(sed "\$d" "$1"); TMP=$(mktemp -d -p "$TEST_WORK"); TO=test; update_helper "$2"; echo "update_helper returned $?"' \
        sh "$HELPER" "$1" > "$WORK/direct.log" 2>&1 || true
}
# An installation of this helper and its units in $WORK/direct.
direct_install() {
    rm -rf "$WORK/direct"
    mkdir -p "$WORK/direct/sbin" "$WORK/direct/units" "$WORK/direct/state"
    cp "$HELPER" "$WORK/direct/sbin/contactor-updater"
    cp "$ROOT/install/contactor-updater.service" "$ROOT/install/contactor-updater.path" "$WORK/direct/units/"
}
direct_state() { find "$WORK/direct/sbin" "$WORK/direct/units" -type f -exec md5sum {} + | sort; }
# The copies $1 (see variants) in $WORK/direct-image/root/app/updater, to change before direct_image.
direct_root() {
    rm -rf "$WORK/direct-image"
    mkdir -p "$WORK/direct-image/root/app/updater"
    cp "$WORK/variants/$1/"* "$WORK/direct-image/root/app/updater/"
}
# An image with what is in $WORK/direct-image/root, tagged $1 and pushed; prints it by digest.
direct_image() {
    printf 'FROM busybox:1.36\nCOPY root/ /\n' > "$WORK/direct-image/Dockerfile"
    docker build --quiet -t "$REPOSITORY:$1" "$WORK/direct-image" > /dev/null
    docker push --quiet "$REPOSITORY:$1" > /dev/null
    docker image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$REPOSITORY:$1" | grep "^$REPOSITORY@" | head -n 1
}

direct_install
before=$(direct_state)
direct_root newer
rm "$WORK/direct-image/root/app/updater/contactor-updater.sh"
ln -s /bin/sh "$WORK/direct-image/root/app/updater/contactor-updater.sh"
direct "$(direct_image direct-link)"
check "a link as the image's helper: refused, nothing replaced" \
    test "$(direct_state)" = "$before" -a "$(grep -c "didn't pass the check" "$WORK/direct.log")" = 1
direct_root newer
rm "$WORK/direct-image/root/app/updater/contactor-updater.sh"
mkdir "$WORK/direct-image/root/app/updater/contactor-updater.sh"
cp "$WORK/variants/newer/contactor-updater.sh" "$WORK/direct-image/root/app/updater/contactor-updater.sh/"
direct "$(direct_image direct-folder)"
check "a folder as the image's helper: refused, nothing replaced" \
    test "$(direct_state)" = "$before" -a "$(grep -c "didn't pass the check" "$WORK/direct.log")" = 1

direct_root newer
printf '[Service]\nExecStart=/bin/true\n' > "$WORK/direct-image/root/app/updater/contactor-updater.service"
direct "$(direct_image direct-unit)"
check "a good helper with a unit that fails its check: the helper goes in" \
    cmp -s "$WORK/direct/sbin/contactor-updater" "$WORK/variants/newer/contactor-updater.sh"
check "that unit stays as it was" cmp -s "$WORK/direct/units/contactor-updater.service" "$ROOT/install/contactor-updater.service"
check "the other unit goes in" cmp -s "$WORK/direct/units/contactor-updater.path" "$WORK/variants/newer/contactor-updater.path"
check "the log says which unit was refused" grep -q "contactor-updater.service in test didn't pass the check" "$WORK/direct.log"

direct_install
rm "$WORK/direct/sbin/contactor-updater"
touch "$WORK/direct/sbin/contactor-updater.new.Ab12Cd" "$WORK/direct/units/contactor-updater.service.new.Ef34Gh"
# What a run that was stopped while it made its container leaves behind.
docker create --name "$READER" busybox:1.36 > /dev/null
direct_root newer2
direct "$(direct_image direct-missing)"
check "no helper in place: it doesn't stop halfway" grep -qx 'update_helper returned 0' "$WORK/direct.log"
check "and the new helper goes in" cmp -s "$WORK/direct/sbin/contactor-updater" "$WORK/variants/newer2/contactor-updater.sh"
check "the log says from an unknown version" grep -q 'brought up to date (version ? to 903)' "$WORK/direct.log"
check "half-written copies a power cut left are removed" test -z "$(find "$WORK/direct" -name '*.new.*')"
check "a container a stopped run left behind is removed too" test -z "$(docker ps -aq --filter "name=^$READER\$")"
for tag in direct-link direct-folder direct-unit direct-missing; do
    docker image rm "$REPOSITORY:$tag" > /dev/null 2>&1 || true
done

say "install.sh --uninstall removes the helper's own copies"
mkdir -p "$WORK/uninstall/sbin" "$WORK/uninstall/units"
for file in sbin/contactor-updater units/contactor-updater.service units/contactor-updater.path; do
    for suffix in "" .previous .new .new.Ab12Cd; do
        touch "$WORK/uninstall/$file$suffix"
    done
done
{
    printf 'HELPER=%s\nUNITS=%s\nHELPER_STATE=%s\nDATA=%s\nAPP_UID=1654\n' "$WORK/uninstall/sbin/contactor-updater" \
        "$WORK/uninstall/units" "$WORK/uninstall/state" "$WORK/uninstall/data"
    # shellcheck disable=SC2016 # for the inner shell
    printf '%s\n' 'as_root() { "$@"; }' 'has_systemd() { false; }' 'ok() { :; }'
    sed -n '/^remove_helper() {/,/^}/p' "$ROOT/install/install.sh"
    printf 'remove_helper\n'
} > "$WORK/uninstall.sh"
sh "$WORK/uninstall.sh"
check "nothing of the helper is left" test -z "$(find "$WORK/uninstall" -type f)"

say "install.sh takes compose.yml and the update helper from the image, never from a release"
# install.sh's own functions on folders of the test's own ($1), as root, with the test's systemctl and a curl and wget
# that only note they were asked; the box at the end is left out (the stand-in app has no setup-status).
installer() {
    local folder=$1
    mkdir -p "$folder/sbin" "$folder/units"
    {
        sed '$d' "$ROOT/install/install.sh"
        printf 'DIR=%s\nDATA=%s\nHELPER=%s\nUNITS=%s\nHELPER_STATE=%s\n' "$folder/opt" "$folder/opt/data" \
            "$folder/sbin/contactor-updater" "$folder/units" "$folder/state"
        # shellcheck disable=SC2016 # for the inner shell
        printf '%s\n' 'as_root() { "$@"; }' 'has_systemd() { true; }' 'show() { echo "(the box)"; }' \
            'TMP=$(mktemp -d)' 'trap cleanup EXIT' 'install'
    } > "$WORK/installer.sh"
    local result=0
    env PATH="$WORK/no-downloads:$WORK/bin:$PATH" TMPDIR="$WORK" CONTACTOR_IMAGE="$REPOSITORY:latest" \
        sh "$WORK/installer.sh" > "$WORK/install.log" 2>&1 || result=$?
    cat "$WORK/install.log" >> "$WORK/installs.log"
    return "$result"
}
mkdir -p "$WORK/no-downloads"
for tool in curl wget; do
    printf '#!/bin/sh\necho "%s $*" >> "%s"\nexit 1\n' "$tool" "$WORK/downloads.log" > "$WORK/no-downloads/$tool"
    chmod 755 "$WORK/no-downloads/$tool"
done
check "install.sh's image is the one compose.yml names" \
    grep -qF "image: \${CONTACTOR_IMAGE:-$(sed -n 's/^IMAGE="\(.*\)"$/\1/p' "$ROOT/install/install.sh")}" "$ROOT/install/compose.yml"
docker compose --project-directory "$DIR" -f "$DIR/compose.yml" down --timeout 1 > /dev/null 2>&1
INST=$WORK/inst
V15=$(release v15 unsigned)
temporary=$(find "$WORK" -maxdepth 1 -name 'tmp.*' | sort)
check "a new installation" installer "$INST"
check "it took compose.yml from the image" cmp -s "$INST/opt/compose.yml" "$ROOT/install/compose.yml"
check "the helper and its units from the image" bash -c "cmp -s '$INST/sbin/contactor-updater' '$WORK/variants/newer2/contactor-updater.sh' \
    && cmp -s '$INST/units/contactor-updater.service' '$WORK/variants/newer2/contactor-updater.service' \
    && cmp -s '$INST/units/contactor-updater.path' '$WORK/variants/newer2/contactor-updater.path'"
check "the helper is executable" test -x "$INST/sbin/contactor-updater"
check "the app runs that image" test -n "$(running_image)" -a "$(running_image)" = "$(image_id "$V15")"
check "the data folder is the app's" test "$(stat -c %u "$INST/opt/data")" = 1654
check "nothing was downloaded with curl or wget" test ! -e "$WORK/downloads.log"
check "no container it made to read the image is left" test -z "$(docker ps -aq --filter "name=^contactor-install-read\$")"
check "no half-written file is left" test -z "$(find "$INST" -name '*.new')"
check "its temporary folders are gone" test "$(find "$WORK" -maxdepth 1 -name 'tmp.*' | sort)" = "$temporary"

say "install.sh again, with a newer helper in the newest image: the helper is brought up to date, nothing else"
before=$(docker inspect -f '{{.Id}}' contactor)
echo '# (test) changed by hand' >> "$INST/opt/compose.yml"
V10=$(release v10 unsigned)
check "install.sh runs again" installer "$INST"
check "it says the helper is brought up to date" grep -q "Update helper brought up to date" "$WORK/install.log"
check "the helper is the newest image's" cmp -s "$INST/sbin/contactor-updater" "$WORK/variants/newer/contactor-updater.sh"
check "compose.yml stays as it was" grep -qx '# (test) changed by hand' "$INST/opt/compose.yml"
check "the same container runs on" test "$(docker inspect -f '{{.Id}}' contactor)" = "$before"
check "latest names the running version again" test "$(image_id "$REPOSITORY:latest")" = "$(image_id "$V15")"
check "the newest image isn't kept" test -z "$(image_id "$V10")"
check "nothing was downloaded with curl or wget" test ! -e "$WORK/downloads.log"

say "install.sh again, with a helper bash can't read in the newest image: it stops, nothing changed"
release v11 unsigned > /dev/null
before=$(find "$INST/sbin" "$INST/units" -type f -exec md5sum {} + | sort)
if installer "$INST"; then
    fail "install.sh took a broken helper"
else
    check "it says the helper isn't Contactor's" grep -q "contactor-updater.sh in Contactor's image (/app/updater) isn't Contactor's update helper" "$WORK/install.log"
fi
check "nothing of the helper changed" test "$(find "$INST/sbin" "$INST/units" -type f -exec md5sum {} + | sort)" = "$before"
check "latest names the running version again" test "$(image_id "$REPOSITORY:latest")" = "$(image_id "$V15")"

say "install.sh again, with no helper in the newest image: the one in place stays"
V13=$(release v13 unsigned)
check "install.sh runs again" installer "$INST"
check "it says so" grep -q "brings no update helper: the one in place stays" "$WORK/install.log"
check "nothing of the helper changed" test "$(find "$INST/sbin" "$INST/units" -type f -exec md5sum {} + | sort)" = "$before"

say "A new installation from an image without compose.yml: it stops, and says why"
docker compose --project-directory "$INST/opt" -f "$INST/opt/compose.yml" down --timeout 1 > /dev/null 2>&1
release v13 unsigned > /dev/null
if installer "$WORK/inst2"; then
    fail "install.sh installed without a compose.yml"
else
    check "it says the image has no compose.yml" grep -q "is too old for this installer: its image has no compose.yml" "$WORK/install.log"
fi
check "there is no compose.yml" test ! -e "$WORK/inst2/opt/compose.yml"
check "no container it made to read the image is left" test -z "$(docker ps -aq --filter "name=^contactor-install-read\$")"
check "nothing was downloaded with curl or wget" test ! -e "$WORK/downloads.log"
docker image rm "$REPOSITORY:latest" "$V13" > /dev/null 2>&1 || true

say "Secrets across the 2000-character cut"
stripped=$(printf '%s Zq7RmW2xKp9LtVb4Nc8YhJ3sDf6GuE1aQw5 after\n' "$(head -c 1990 /dev/zero | tr '\0' a)" \
    | bash -c "MAX_LINE=2000; FIRST_CUT=8000; $(sed -n '/^strip_secrets() {/,/^}/p' "$HELPER"); strip_secrets")
check "a token across the cut is hidden whole" bash -c "[[ '$stripped' == *' [hidden] …' && '$stripped' != *Zq* ]]"
strip() {
    bash -c "MAX_LINE=2000; FIRST_CUT=8000; $(sed -n '/^strip_secrets() {/,/^}/p' "$HELPER"); strip_secrets"
}
stripped=$(printf 'x eyJ%s.sig %s Zq7RmW2xKp9LtVb4Nc8YhJ3sDf6GuE1aQw5 after\n' "$(head -c 7000 /dev/zero | tr '\0' A)" "$(head -c 970 /dev/zero | tr '\0' a)" | strip)
check "a token across the first cut (8000) is dropped, and the line says it was cut" \
    test "$stripped" = "x [hidden] $(head -c 970 /dev/zero | tr '\0' a) …"
# The worst lines for the patterns, each 8000 characters: a generous bound, far above the few milliseconds they take.
worst=$( (head -c 8000 /dev/zero | tr '\0' a; echo; head -c 20000 /dev/urandom | base64 -w0 | tr '[:upper:]' '[:lower:]' | tr -d '/+=' | head -c 8000; echo;
    for _ in $(seq 1 800); do printf 'keykeykeyk'; done; echo) | timeout 10 bash -c "MAX_LINE=2000; FIRST_CUT=8000; $(sed -n '/^strip_secrets() {/,/^}/p' "$HELPER"); strip_secrets" | wc -l)
check "three 8000-character worst-case lines are stripped well within 10 s" test "$worst" = 3

printf '\nThe helper said:\n'
if [[ "$FAILED" -gt 0 ]]; then
    sed 's/^/  | /' "$WORK/helper.log"
    printf '\ninstall.sh said:\n'
    sed 's/^/  | /' "$WORK/installs.log" 2> /dev/null || true
else
    sed 's/^/  | /' "$WORK/helper.log" | tail -n 40
fi
if [[ "$FAILED" -gt 0 ]]; then
    printf '\n%s check(s) failed.\n' "$FAILED"
    exit 1
fi
printf '\nEvery check passed.\n'
