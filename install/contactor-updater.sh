#!/usr/bin/env bash
# Contactor's update helper: updates Contactor when someone presses "Update now" in its Settings, and goes back to the
# version before when the new one doesn't come up. install.sh puts it in /usr/local/sbin/contactor-updater, with
# contactor-updater.service (which runs it) and contactor-updater.path (which starts the service when the app asks).
# It runs as root, because it uses Docker; the app runs in its container and never gets the Docker socket.
#
# The app and this helper talk only through files in Contactor's data folder (/opt/contactor/data, the app's /data):
#
#   updates/request    the app writes "update" (or "cancel" while an update is under way). Nothing else in it counts.
#   updates/status     this helper writes how far the update is, and how it ended; the Settings page shows it.
#   updates/helper     this helper writes it when it runs, so the app knows it is installed.
#   updates/logs/      a log per update, the latest 10, with secrets stripped; "Show what happened" shows it.
#   updates/before-update.db   the database as it was before the update, made by the app itself (update-backup).
#
# The app's user (1654) owns that folder, so everything there is untrusted: this helper reads and writes it only as
# that user (setpriv), never as root, and takes nothing from it but the one word. It always updates to the newest
# release (the image's "latest" tag), never to an image, tag or command a file names, and runs only an image signed
# with one of our keys, checked by digest. What it needs to go back (the image digest before) is kept where only
# root can write: /var/lib/contactor-updater/journal. After a power cut, the next run finds that journal and finishes
# the update or goes back.
#
# One update at a time: systemd runs one copy of the service, and a lock keeps a second run out.
#
# For testing only (scripts/updater-test.sh): CONTACTOR_DIR, CONTACTOR_REPOSITORY, CONTACTOR_COSIGN,
# CONTACTOR_COSIGN_KEYS (public keys, separated by empty lines), CONTACTOR_UPDATER_STATE, CONTACTOR_HEALTH_SECONDS,
# CONTACTOR_BOOT_ID (this start's id), CONTACTOR_STUCK_SECONDS, CONTACTOR_WAIT_SECONDS and CONTACTOR_TEST_STOP_AT (stops the run dead at a
# step, as a power cut would). They come from the service's own
# environment, which only root can set, never from the app.
set -euo pipefail
umask 022

readonly HELPER_VERSION=1
readonly DIR=${CONTACTOR_DIR:-/opt/contactor}
readonly DATA=$DIR/data
readonly UPDATES=$DATA/updates
readonly STATE=${CONTACTOR_UPDATER_STATE:-/var/lib/contactor-updater}
readonly JOURNAL=$STATE/journal
readonly REPOSITORY=${CONTACTOR_REPOSITORY:-ghcr.io/gonace/contactor}
# The image's user, who owns the data folder.
readonly APP_UID=1654
# How long the new version has to become healthy, in seconds.
readonly HEALTH_SECONDS=${CONTACTOR_HEALTH_SECONDS:-300}
# A request older than this (in seconds) was left behind, e.g. while the helper wasn't running: it is never acted on.
readonly REQUEST_MAX_AGE=120
# How long the update waits, just before the restart, for a group that is starting or running on; then it stops with
# nothing changed. update-backup says so with its exit code (UpdateBackup.GroupsBusyExitCode); asked every 10 seconds.
readonly WAIT_SECONDS=${CONTACTOR_WAIT_SECONDS:-600}
readonly GROUPS_BUSY=3
# How long a file in the app's folder may take to read: a FIFO there would otherwise hold the helper for ever.
readonly READ_SECONDS=5
# A request the helper can't remove would start it again and again (contactor-updater.path): it waits this long first.
readonly STUCK_SECONDS=${CONTACTOR_STUCK_SECONDS:-60}
# This start of the machine: the first run after a start takes no request (see take_request).
readonly BOOT_ID=${CONTACTOR_BOOT_ID:-$(cat /proc/sys/kernel/random/boot_id 2> /dev/null || echo unknown)}
# The longest line the log keeps, and the first cut that keeps stripping a huge one cheap; the app's UpdateLog cuts
# at the same lengths.
readonly MAX_LINE=2000
readonly FIRST_CUT=8000
readonly KEEP_LOGS=10
# Room kept free beyond what the update needs, in MB.
readonly SPARE_MB=200

# cosign checks the image's signature. It runs as a container, so nothing is installed on the machine.
readonly COSIGN=${CONTACTOR_COSIGN:-ghcr.io/sigstore/cosign/cosign:v3.1.4@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d}
# The public keys our images are signed with, as in deploy/cosign: the main key, and the reserve key whose private
# half is kept offline. scripts/images-sign.sh refuses to sign with a key that isn't here.
COSIGN_KEYS=(
"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPRbttsnxj+3I6BPt+aRzSluhz9Un
gXBxyvK5NYdVG/ywtSyKmrV6fZ90OM0CJb4aTugO0p6QU7fyh3weYfyYjg==
-----END PUBLIC KEY-----"
"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/L6B3FW9vgpVpMZ9WkkUZYoriYJQ
58dJAHz8BXwrkuF99WS2Pv10Kx8L4IPTIYZbrEcIom6DAkeqWi6kZMqpMQ==
-----END PUBLIC KEY-----"
)
if [[ -n "${CONTACTOR_COSIGN_KEYS:-}" ]]; then
    COSIGN_KEYS=()
    key=""
    while IFS= read -r line || [[ -n "$line" ]]; do
        if [[ -z "$line" ]]; then
            [[ -z "$key" ]] || COSIGN_KEYS+=("$key")
            key=""
        else
            key+=${key:+$'\n'}$line
        fi
    done <<< "$CONTACTOR_COSIGN_KEYS"
    [[ -z "$key" ]] || COSIGN_KEYS+=("$key")
fi
readonly COSIGN_KEYS

# The update under way: its id, the log, the versions and the images (by digest).
ID=""
LOG=""
FROM=""
TO=""
PREVIOUS=""
NEW=""
STARTED=""
PHASE=""
TMP=""
# Set on the first run since the machine started.
FIRST_SINCE_START=""

# --- Files in the data folder, always as the app's user ----------------------------------------------------------

# Runs a command as the app's user, so a symlink or anything else the app put in its folder reaches only what the app
# could reach itself.
as_app() {
    setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs -- "$@"
}

# Prints at most $2 bytes of a file in the app's folder, as the app's user: only a regular file, not a link, a FIFO or
# a device, and never for longer than READ_SECONDS. Fails when it isn't one to read.
read_app_file() {
    local path=$1 bytes=$2
    # shellcheck disable=SC2016 # expanded by the inner shell
    timeout "$READ_SECONDS" setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs -- \
        sh -c '[ -f "$1" ] && [ ! -L "$1" ] && head -c "$2" -- "$1"' sh "$path" "$bytes" 2> /dev/null
}

# Writes standard input to a file in the updates folder, as the app's user, in one step: the app never reads half a file.
write_app_file() {
    # shellcheck disable=SC2016 # expanded by the inner shell
    as_app sh -c 'tmp=$(mktemp "$1.XXXXXX") && cat > "$tmp" && chmod 644 "$tmp" && mv -f "$tmp" "$1"' sh "$1"
}

# Leaves out anything that looks like a secret: the tunnel token, Tailscale's auth key, a password, a key or a token
# (Contactor.Web's UpdateLog.Strip follows the same rules). Digests (hex) and ordinary words and paths stay, so the log
# still says which image was which.
strip_secrets() {
    local run="" i
    for ((i = 0; i < 24; i++)); do
        run+="[A-Za-z0-9+=_-]"
    done
    awk -v run="$run" -v q="'" -v max="$MAX_LINE" -v first="$FIRST_CUT" '
        BEGIN {
            # From the word that makes it the name of a secret: the letters before it stay as they are either way, and
            # without a run of them in front, matching costs no more than the line is long.
            name = "(token|key|password|passwd|pwd|secret|auth)[a-z0-9_.-]*[\"" q "]?[ \t]*[:=][ \t]*[\"" q "]?"
            value = "^[^ \t\"" q ",;&]+"
        }
        function hide(text, pattern,    out) {
            out = ""
            while (match(text, pattern)) {
                out = out substr(text, 1, RSTART - 1) "[hidden]"
                text = substr(text, RSTART + RLENGTH)
            }
            return out text
        }
        # A long run of letters and digits that looks random (a digit, a capital and a small letter), e.g. a token.
        function long_runs(text,    out, piece) {
            out = ""
            while (match(text, run "[A-Za-z0-9+=_-]*")) {
                piece = substr(text, RSTART, RLENGTH)
                out = out substr(text, 1, RSTART - 1) (piece ~ /[0-9]/ && piece ~ /[A-Z]/ && piece ~ /[a-z]/ ? "[hidden]" : piece)
                text = substr(text, RSTART + RLENGTH)
            }
            return out text
        }
        # name=value or name: value, for a name with token, key, password, secret or auth in it: the value goes.
        function named(text,    out, lower, kept) {
            out = ""
            lower = tolower(text)
            while (match(lower, name)) {
                kept = substr(text, 1, RSTART + RLENGTH - 1)
                text = substr(text, RSTART + RLENGTH)
                lower = substr(lower, RSTART + RLENGTH)
                if (match(lower, value)) {
                    kept = kept "[hidden]"
                    text = substr(text, RLENGTH + 1)
                    lower = substr(lower, RLENGTH + 1)
                }
                out = out kept
            }
            return out text
        }
        {
            # Secrets are hidden before the line is cut, so none is cut in half and left showing. A first, longer cut
            # keeps a huge line cheap; it drops the word it cuts through, which could be the start of a secret.
            cut = 0
            line = $0
            if (length(line) > first) {
                line = substr(line, 1, first)
                for (i = first; i > 0 && substr(line, i, 1) != " " && substr(line, i, 1) != "\t"; i--) { }
                line = substr(line, 1, i)
                cut = 1
            }
            line = hide(line, "tskey-[A-Za-z0-9-]+")
            line = hide(line, "eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_.-]*")
            line = hide(line, "[Bb][Ee][Aa][Rr][Ee][Rr] [^ \t]+")
            line = hide(line, "[Bb][Aa][Ss][Ii][Cc] [^ \t]+")
            line = named(line)
            line = long_runs(line)
            print (length(line) > max ? substr(line, 1, max) "…" : cut ? line "…" : line)
        }'
}

# Appends standard input to this update's log, as the app's user.
to_log() {
    if [[ -n "$LOG" ]]; then
        # shellcheck disable=SC2016 # expanded by the inner shell
        timeout "$READ_SECONDS" setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs -- \
            sh -c 'cat >> "$1"' sh "$LOG" 2> /dev/null || true
    else
        cat > /dev/null
    fi
}

# A line in this update's log, with the time, and in the service's own log (journalctl -u contactor-updater), on
# standard error: standard output carries what a function returns.
log() {
    local line
    line=$(printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*" | strip_secrets)
    printf '%s\n' "$line" >&2
    printf '%s\n' "$line" | to_log
}

# Runs a command, putting the last 200 lines of what it says into the log; returns how it ended.
run_logged() {
    local status=0
    "$@" > "$TMP/out" 2>&1 || status=$?
    if [[ -s "$TMP/out" ]]; then
        tail -n 200 "$TMP/out" | sed 's/^/    /' | strip_secrets > "$TMP/clean"
        cat "$TMP/clean" >&2
        to_log < "$TMP/clean"
    fi
    return "$status"
}

# Only plain version words ever reach the status file: 2026.11.20, 2026.11.20.1 or 0.0.0-dev.
clean_version() {
    if [[ "$1" =~ ^[0-9]{1,4}(\.[0-9]{1,4}){1,3}(-dev)?$ ]]; then
        printf '%s' "$1"
    fi
}

now() {
    date -u +%Y-%m-%dT%H:%M:%SZ
}

# The status file the Settings page reads: key=value lines. $1: running, done, went-back, failed or current; then
# step=…, reason=… and other key=value pairs.
status() {
    local state=$1
    shift
    {
        printf 'helper=%s\n' "$HELPER_VERSION"
        printf 'id=%s\n' "$ID"
        printf 'state=%s\n' "$state"
        printf 'from=%s\n' "$FROM"
        printf 'to=%s\n' "$TO"
        printf 'started=%s\n' "$STARTED"
        printf 'changed=%s\n' "$(now)"
        local pair
        for pair in "$@"; do
            printf '%s\n' "$pair"
        done
    } | write_app_file "$UPDATES/status" || log "Couldn't write the status file."
}

# --- The root-only journal: what this helper needs to finish or go back after a power cut --------------------------

# Fails (returns 1) when it can't be written, e.g. on a full disk: then nothing may go further.
journal_write() {
    local phase=$1
    if ! printf 'phase=%s\nid=%s\nprevious=%s\nnew=%s\nfrom=%s\nto=%s\nstarted=%s\n' \
        "$phase" "$ID" "$PREVIOUS" "$NEW" "$FROM" "$TO" "$STARTED" > "$JOURNAL.tmp" 2> /dev/null; then
        log "Couldn't write the journal in $STATE (is the disk full?)."
        return 1
    fi
    sync "$JOURNAL.tmp" 2> /dev/null || true
    mv -f "$JOURNAL.tmp" "$JOURNAL" || return 1
    sync "$STATE" 2> /dev/null || true
}

journal_clear() {
    rm -f "$JOURNAL" "$JOURNAL.tmp"
}

# Reads the journal into the globals, checking every value, and its phase into PHASE: empty when there is none or it
# is damaged (a damaged one is removed: there is nothing safe to do with it).
journal_read() {
    PHASE=""
    [[ -f "$JOURNAL" ]] || return 0
    local key value phase=""
    while IFS='=' read -r key value; do
        case "$key" in
            phase) [[ "$value" =~ ^(prepare|restart|check|back)$ ]] && phase=$value ;;
            id) [[ "$value" =~ ^[0-9]{8}-[0-9]{6}$ ]] && ID=$value ;;
            previous) is_reference "$value" && PREVIOUS=$value ;;
            new) [[ -z "$value" ]] || { is_reference "$value" && NEW=$value; } ;;
            from) FROM=$(clean_version "$value") ;;
            to) TO=$(clean_version "$value") ;;
            started) [[ "$value" =~ ^[0-9T:Z-]{20}$ ]] && STARTED=$value ;;
        esac
    done < "$JOURNAL"
    if [[ -z "$phase" || -z "$ID" || -z "$PREVIOUS" ]] || { [[ "$phase" != prepare ]] && [[ -z "$NEW" ]]; }; then
        log "The journal of an earlier update can't be read; it is removed."
        journal_clear
        return 0
    fi
    PHASE=$phase
}

# --- Docker ------------------------------------------------------------------------------------------------------

# An image of our repository by its digest: the only kind this helper ever starts.
is_reference() {
    [[ "$1" == "$REPOSITORY@sha256:"* && "$1" =~ @sha256:[0-9a-f]{64}$ ]]
}

compose() {
    docker compose --project-directory "$DIR" -f "$DIR/compose.yml" "$@"
}

# The app's container, if there is one.
container() {
    compose ps --all --quiet contactor 2> /dev/null | head -n 1 || true
}

# The image (repository@digest) an image id was pulled as, or nothing.
reference_of() {
    docker image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$1" 2> /dev/null \
        | grep -E "^$(printf '%s' "$REPOSITORY" | sed 's/[.]/\\./g')@sha256:[0-9a-f]{64}$" | head -n 1 || true
}

# The release an image or container is, from the label the Dockerfile sets.
version_of() {
    clean_version "$(docker inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2> /dev/null || true)"
}

# Whether version $1 is newer than $2 (2026.11.20 > 2026.11.14; a development build is older than any release).
newer() {
    [[ "$1" != *-dev ]] || return 1
    [[ "$2" != *-dev ]] || return 0
    awk -v a="$1" -v b="$2" 'BEGIN {
        n = split(a, x, "."); m = split(b, y, "."); k = n > m ? n : m
        for (i = 1; i <= k; i++) {
            p = i <= n ? x[i] + 0 : 0; q = i <= m ? y[i] + 0 : 0
            if (p != q) { exit p > q ? 0 : 1 }
        }
        exit 1
    }'
}

# Whether the image (repository@digest) is signed with one of COSIGN_KEYS; logs why not.
signed() {
    local reference=$1 key output first=""
    # Pinned by digest, so one already here is the same: downloaded only once.
    if ! docker image inspect "$COSIGN" > /dev/null 2>&1 && ! run_logged docker pull --quiet "$COSIGN"; then
        log "Couldn't download cosign ($COSIGN)."
        return 1
    fi
    for key in "${COSIGN_KEYS[@]}"; do
        if output=$(docker run --rm --network host --env COSIGN_KEY="$key" "$COSIGN" \
            verify --key env://COSIGN_KEY --insecure-ignore-tlog=true "$reference" 2>&1); then
            log "$reference is signed with our key."
            return 0
        fi
        first=${first:-$output}
    done
    log "$reference isn't signed with our key. cosign says:"
    printf '%s\n' "$first" | grep -v -e 'Skipping tlog verification' -e '^$' | tail -n 20 | sed 's/^/    /' \
        | strip_secrets | tee /dev/stderr | to_log
    return 1
}

# Starts the app on exactly this image, by digest; never pulls.
start_on() {
    CONTACTOR_IMAGE=$1 run_logged compose up --detach --no-build --pull never --remove-orphans --timeout 30
}

# The container's state, restart count and health, e.g. "running 0 healthy".
state_of() {
    docker inspect -f '{{.State.Status}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2> /dev/null || true
}

# Waits until the app's container is healthy; fails when it stops, restarts or its health check fails, or after
# HEALTH_SECONDS.
wait_healthy() {
    local cid waited=0 restarts state
    cid=$(container)
    [[ -n "$cid" ]] || return 1
    restarts=$(state_of "$cid" | awk '{ print $2 }')
    while [[ "$waited" -lt "$HEALTH_SECONDS" ]]; do
        state=$(state_of "$cid")
        # shellcheck disable=SC2086 # three words, split on purpose
        set -- $state
        case "${3:-}" in
            healthy) return 0 ;;
        esac
        case "${1:-}" in
            exited | dead | restarting)
                log "It stopped ($state)."
                return 1
                ;;
        esac
        if [[ -n "${2:-}" && -n "$restarts" && "$2" -gt "$restarts" ]]; then
            log "It restarted ($state)."
            return 1
        fi
        if [[ "${3:-}" == unhealthy ]]; then
            log "Its health check fails."
            return 1
        fi
        sleep 2
        waited=$((waited + 2))
    done
    log "It wasn't healthy after $HEALTH_SECONDS seconds."
    return 1
}

# Free space in MB on the fuller of the folders the images and the data go to.
free_mb() {
    local least="" folder kb root
    root=$(docker info -f '{{.DockerRootDir}}' 2> /dev/null || echo /var/lib/docker)
    # Docker's containerd image store keeps the images in containerd's own folder.
    for folder in "$root" /var/lib/containerd "$DATA"; do
        [[ "$folder" != /var/lib/containerd || -d "$folder" ]] || continue
        while [[ ! -d "$folder" ]]; do
            folder=$(dirname "$folder")
        done
        kb=$(df -Pk "$folder" 2> /dev/null | awk 'NR == 2 { print $4 }')
        [[ "$kb" =~ ^[0-9]+$ ]] || continue
        if [[ -z "$least" || "$kb" -lt "$least" ]]; then
            least=$kb
        fi
    done
    echo $(((${least:-0}) / 1024))
}

# What the update needs, in MB: the new image (about as big as the running one), the database's backup, and spare room.
needed_mb() {
    local image_bytes db_bytes
    image_bytes=$(docker image inspect -f '{{.Size}}' "$1" 2> /dev/null || echo 0)
    [[ "$image_bytes" =~ ^[0-9]+$ ]] || image_bytes=0
    db_bytes=$(as_app stat -c %s -- "$DATA/contactor.db" 2> /dev/null || echo 0)
    [[ "$db_bytes" =~ ^[0-9]+$ ]] || db_bytes=0
    echo $(((image_bytes + 2 * db_bytes) / 1024 / 1024 + SPARE_MB))
}

# Removes the app's images but the running one and the one before it, so the SD card doesn't fill up: each by its
# names, then by its id. Docker itself refuses to remove an image a container uses.
remove_old_images() {
    local keep_new=$1 keep_previous=$2 id name
    while read -r id; do
        [[ -n "$id" && "$id" != "$keep_new" && "$id" != "$keep_previous" ]] || continue
        while read -r name; do
            [[ -z "$name" ]] || docker image rm "$name" > /dev/null 2>&1 || true
        done < <(docker image inspect -f '{{range .RepoTags}}{{println .}}{{end}}{{range .RepoDigests}}{{println .}}{{end}}' "$id" 2> /dev/null)
        docker image rm "$id" > /dev/null 2>&1 || true
        if ! docker image inspect "$id" > /dev/null 2>&1; then
            log "Removed an older version ($id)."
        fi
    done < <(docker image ls --no-trunc --format '{{.ID}}' "$REPOSITORY" | awk '!seen[$0]++')
}

# Keeps the latest KEEP_LOGS logs.
prune_logs() {
    local file
    as_app find "$UPDATES/logs" -maxdepth 1 -type f -name 'update-*.log' 2> /dev/null | sort | head -n "-$KEEP_LOGS" \
        | while IFS= read -r file; do
            as_app rm -f -- "$file" || true
        done
}

# For testing only: stops the run dead here, as a power cut would.
power_cut_at() {
    if [[ "${CONTACTOR_TEST_STOP_AT:-}" == "$1" ]]; then
        log "(test) The power goes off."
        kill -9 $$
    fi
}

# --- The request -------------------------------------------------------------------------------------------------

# Reads the request and removes it, as the app's user. Prints its one word when it is fresh, nothing otherwise.
take_request() {
    local path=$UPDATES/request word="" mtime age
    as_app test -e "$path" 2> /dev/null || as_app test -L "$path" 2> /dev/null || return 0
    mtime=$(as_app stat -c %Y -- "$path" 2> /dev/null || echo 0)
    word=$(read_app_file "$path" 16 | head -n 1 | tr -d '\r' || true)
    # Anything there goes, a folder too; as the app's user, so nothing else can.
    as_app rm -rf -- "$path" 2> /dev/null || true
    if as_app test -e "$path" 2> /dev/null || as_app test -L "$path" 2> /dev/null; then
        # contactor-updater.path would start the helper again at once, until systemd gives up on it.
        log "Couldn't remove the request; waiting $STUCK_SECONDS seconds before the next try."
        sleep "$STUCK_SECONDS"
        return 0
    fi
    if [[ -n "$FIRST_SINCE_START" ]]; then
        # The machine's clock may be wrong at first (a Raspberry Pi has none of its own), so a request's age can't be
        # told: none is taken. The app took it back long ago anyway.
        log "A request from before the machine started is ignored."
        return 0
    fi
    [[ "$mtime" =~ ^[0-9]+$ ]] || mtime=0
    age=$(($(date +%s) - mtime))
    if [[ "$age" -gt "$REQUEST_MAX_AGE" || "$age" -lt "-$REQUEST_MAX_AGE" ]]; then
        log "A request from $age seconds ago was left behind; it is ignored."
        return 0
    fi
    case "$word" in
        update | cancel) printf '%s' "$word" ;;
        *) log "The request isn't one this helper knows; it is ignored." ;;
    esac
}

# Whether "cancel" was asked while the update is under way; the request is taken either way.
cancelled() {
    [[ "$(take_request)" == cancel ]]
}

# --- The update --------------------------------------------------------------------------------------------------

PULL_CANCELLED=0

# Pulls $1, checking for "cancel" every second. The docker client is this shell's own child, so stopping it stops
# the pull (the daemon gives up a pull nobody waits for); this waits until it is really gone.
pull() {
    local pid status=0
    PULL_CANCELLED=0
    docker pull --quiet "$1" > "$TMP/pull" 2>&1 &
    pid=$!
    while kill -0 "$pid" 2> /dev/null; do
        if cancelled; then
            PULL_CANCELLED=1
            kill "$pid" 2> /dev/null || true
            wait "$pid" 2> /dev/null || true
            return 1
        fi
        sleep 1
    done
    wait "$pid" || status=$?
    return "$status"
}

# Puts "latest" back on the running version, and checks that it stays there: a pull the daemon was finishing as it
# was stopped could still move it.
put_latest_back() {
    local want i
    want=$(docker image inspect -f '{{.Id}}' "$PREVIOUS" 2> /dev/null || true)
    for i in 1 2 3 4 5; do
        docker tag "$PREVIOUS" "$REPOSITORY:latest" 2> /dev/null || true
        sleep 1
        if [[ -n "$want" && "$(docker image inspect -f '{{.Id}}' "$REPOSITORY:latest" 2> /dev/null || true)" == "$want" ]]; then
            [[ "$i" -lt 2 ]] || log "\"latest\" names ${FROM:-the running version} again."
            return 0
        fi
    done
    log "Couldn't put \"latest\" back on ${FROM:-the running version}."
}

# Ends an update that stopped before the restart: nothing was changed.
fail_before() {
    local reason=$1
    shift
    log "Nothing was changed: $reason."
    journal_clear
    if [[ -n "$NEW" ]]; then
        docker image rm "$NEW" > /dev/null 2>&1 || true
    fi
    status failed "reason=$reason" "$@"
    return 0
}

update() {
    local cid previous_id new_id need free
    ID=$(date -u +%Y%m%d-%H%M%S)
    STARTED=$(now)
    LOG=$UPDATES/logs/update-$ID.log
    as_app mkdir -p "$UPDATES/logs"
    printf '' | write_app_file "$LOG" || true
    prune_logs
    log "Update asked for (helper $HELPER_VERSION, $REPOSITORY)."

    cid=$(container)
    if [[ -z "$cid" || "$(docker inspect -f '{{.State.Running}}' "$cid" 2> /dev/null || true)" != true ]]; then
        status failed reason=error
        log "Contactor isn't running, so it isn't updated."
        return 0
    fi
    previous_id=$(docker inspect -f '{{.Image}}' "$cid")
    PREVIOUS=$(reference_of "$previous_id")
    FROM=$(version_of "$cid")
    log "Contactor ${FROM:-?} runs ${PREVIOUS:-$previous_id}."
    if [[ -z "$PREVIOUS" ]]; then
        log "The running image wasn't downloaded from $REPOSITORY, so the update couldn't go back to it."
        status failed reason=error
        return 0
    fi
    if ! journal_write prepare; then
        status failed reason=space
        return 0
    fi
    status running step=download

    need=$(needed_mb "$previous_id")
    free=$(free_mb)
    log "Free space: $free MB; the update needs $need MB."
    if [[ "$free" -lt "$need" ]]; then
        fail_before space "need-mb=$need" "free-mb=$free"
        return 0
    fi

    # Each version is kept under its digest: Docker's containerd image store forgets an image's digest once its tag
    # moves on, and going back needs it. For the running version that only fetches its manifest.
    log "Downloading $REPOSITORY:latest."
    local reference
    for reference in "$PREVIOUS" "$REPOSITORY:latest"; do
        if ! pull "$reference"; then
            tail -n 50 "$TMP/pull" | sed 's/^/    /' | strip_secrets | to_log
            # Whatever the pull did before it stopped: "latest" names the running version again.
            put_latest_back
            if [[ "$PULL_CANCELLED" == 1 ]]; then
                fail_before cancelled
            else
                fail_before download
            fi
            return 0
        fi
        tail -n 50 "$TMP/pull" | sed 's/^/    /' | strip_secrets | to_log
    done
    new_id=$(docker image inspect -f '{{.Id}}' "$REPOSITORY:latest")
    NEW=$(reference_of "$new_id")
    if [[ -n "$NEW" && "$new_id" != "$previous_id" ]] && ! run_logged docker pull --quiet "$NEW"; then
        NEW=""
    fi
    # The name goes back on the running version at once: "latest" only ever names an image that was checked, so a
    # plain docker compose up (e.g. by install.sh) never starts one that wasn't.
    docker tag "$PREVIOUS" "$REPOSITORY:latest"
    if [[ "$new_id" == "$previous_id" ]]; then
        log "Contactor ${FROM:-?} is the newest version."
        NEW=""
        TO=$FROM
        journal_clear
        status current
        return 0
    fi
    TO=$(version_of "$new_id")
    log "Downloaded ${TO:-?} ($NEW)."
    if [[ -z "$NEW" ]]; then
        fail_before download
        return 0
    fi
    if [[ -z "$TO" ]] || ! newer "$TO" "$FROM"; then
        log "${TO:-It} isn't newer than ${FROM:-the running version}."
        docker image rm "$NEW" > /dev/null 2>&1 || true
        NEW=""
        TO=$FROM
        journal_clear
        status current
        return 0
    fi
    if ! journal_write prepare; then
        fail_before space
        return 0
    fi
    power_cut_at download

    status running step=verify
    if ! signed "$NEW"; then
        fail_before signature
        return 0
    fi
    if cancelled; then
        fail_before cancelled
        return 0
    fi

    status running step=backup
    log "Backing up the database."
    local waited=0 backed=0
    while :; do
        as_app rm -f -- "$UPDATES/before-update.db" 2> /dev/null || true
        backed=0
        timeout 300 docker exec "$cid" dotnet /app/Contactor.Web.dll update-backup > "$TMP/backup" 2>&1 || backed=$?
        if [[ "$backed" -ne "$GROUPS_BUSY" ]]; then
            # Only the try that counts goes into the log; the waiting ones say the same each time.
            tail -n 200 "$TMP/backup" | sed 's/^/    /' | strip_secrets | to_log
            break
        fi
        # A group is starting or running on: the restart would cut it off. It waits, as long as WAIT_SECONDS.
        if [[ "$waited" -ge "$WAIT_SECONDS" ]]; then
            log "The relays were still switching after $WAIT_SECONDS seconds."
            fail_before switching
            return 0
        fi
        if [[ "$waited" -eq 0 ]]; then
            log "Waiting for the relays to finish switching."
            status running step=waiting
        fi
        sleep 10
        waited=$((waited + 10))
        if cancelled; then
            fail_before cancelled
            return 0
        fi
    done
    if [[ "$waited" -gt 0 ]]; then
        log "The relays are done switching, after $waited seconds."
        status running step=backup
    fi
    if [[ "$backed" -ne 0 ]] || ! as_app test -s "$UPDATES/before-update.db"; then
        fail_before backup
        return 0
    fi
    if cancelled; then
        fail_before cancelled
        return 0
    fi

    # From here on it goes through: no cancel, and a power cut is finished or gone back from at the next start.
    if ! journal_write restart; then
        fail_before space
        return 0
    fi
    status running step=restart
    # The app tells every open page, which shows the splash, before it goes.
    sleep 3
    power_cut_at restart
    log "Restarting Contactor on ${TO} ($NEW)."
    if ! start_on "$NEW"; then
        log "It didn't start."
        go_back health
        return 0
    fi
    finish_check
}

# Waits for the new version to be healthy: done, or back to the one before.
finish_check() {
    journal_write check || true
    status running step=check
    power_cut_at check
    if wait_healthy; then
        log "Contactor $TO is up and healthy."
        docker tag "$NEW" "$REPOSITORY:latest"
        remove_old_images "$(docker image inspect -f '{{.Id}}' "$NEW")" "$(docker image inspect -f '{{.Id}}' "$PREVIOUS" 2> /dev/null || true)"
        journal_clear
        status "done"
        return 0
    fi
    go_back health
}

# Back to the version before, by its digest, with the database as it was before the update.
go_back() {
    local reason=$1 cid
    journal_write back || true
    status running step=back
    cid=$(container)
    if [[ -n "$cid" ]]; then
        log "The last lines of ${TO:-the new version}'s log:"
        run_logged docker logs --tail 200 "$cid" || true
    fi
    log "Going back to ${FROM:-the version before} ($PREVIOUS)."
    run_logged compose stop --timeout 30 contactor || true
    if as_app test -f "$UPDATES/before-update.db" && ! as_app test -L "$UPDATES/before-update.db" \
        && as_app test -s "$UPDATES/before-update.db"; then
        # The WAL files belong to the database being replaced: left behind, SQLite would apply them to the backup.
        # shellcheck disable=SC2016 # expanded by the inner shell
        if timeout 300 setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs -- \
            sh -c 'rm -f "$1-wal" "$1-shm" && cp "$2" "$1.restoring" && mv -f "$1.restoring" "$1"' \
            sh "$DATA/contactor.db" "$UPDATES/before-update.db"; then
            log "The database is back as it was before the update."
        else
            log "Couldn't put the database back."
        fi
    else
        log "There is no backup of the database to put back."
    fi
    docker tag "$PREVIOUS" "$REPOSITORY:latest" 2> /dev/null || true
    if start_on "$PREVIOUS" && wait_healthy; then
        log "Contactor ${FROM:-?} is up again."
    else
        log "Contactor ${FROM:-?} doesn't come up either."
    fi
    if [[ -n "$NEW" ]]; then
        docker image rm "$NEW" > /dev/null 2>&1 || true
    fi
    journal_clear
    status went-back "reason=$reason" "back=$(now)"
}

# An update the last run left unfinished, e.g. after a power cut.
recover() {
    journal_read
    [[ -n "$PHASE" ]] || return 0
    LOG=$UPDATES/logs/update-$ID.log
    log "Found the unfinished update $ID ($PHASE), e.g. after a power cut."
    case "$PHASE" in
        prepare)
            # Before the restart: the app was never touched. Only make sure it runs, and that "latest" names it.
            docker tag "$PREVIOUS" "$REPOSITORY:latest" 2> /dev/null || true
            local cid
            cid=$(container)
            if [[ -z "$cid" || "$(docker inspect -f '{{.State.Running}}' "$cid" 2> /dev/null || true)" != true ]]; then
                start_on "$PREVIOUS" || true
            fi
            fail_before interrupted
            ;;
        restart | check)
            local cid running=""
            cid=$(container)
            if [[ -n "$cid" ]]; then
                running=$(docker inspect -f '{{.Image}}' "$cid" 2> /dev/null || true)
            fi
            if [[ -z "$running" || "$running" != "$(docker image inspect -f '{{.Id}}' "$NEW" 2> /dev/null || true)" ]]; then
                log "Starting ${TO:-the new version} again."
                if ! start_on "$NEW"; then
                    go_back health
                    return 0
                fi
            fi
            finish_check
            ;;
        back)
            go_back health
            ;;
    esac
}

main() {
    if [[ "${1:-}" == --version ]]; then
        echo "$HELPER_VERSION"
        return 0
    fi
    [[ $EUID -eq 0 ]] || { echo "contactor-updater: run it as root." >&2; return 1; }
    [[ ${#COSIGN_KEYS[@]} -gt 0 ]] || { echo "contactor-updater: no signing keys." >&2; return 1; }
    mkdir -p "$STATE"
    chmod 700 "$STATE"
    # Before anything can end this run early: otherwise the first "Update now" after it would count as the first run
    # since the machine started, and be ignored.
    if [[ "$(cat "$STATE/boot" 2> /dev/null || true)" != "$BOOT_ID" ]]; then
        FIRST_SINCE_START=1
        printf '%s\n' "$BOOT_ID" > "$STATE/boot" || true
    fi
    # Not installed (yet, or any more): nothing to do.
    [[ -d "$DATA" && -f "$DIR/compose.yml" ]] || return 0
    exec 9> "$STATE/lock"
    flock --nonblock 9 || return 0
    TMP=$(mktemp -d "$STATE/run.XXXXXX")
    trap 'rm -rf -- "$TMP"' EXIT

    as_app mkdir -p "$UPDATES/logs" || { echo "contactor-updater: can't write to $UPDATES." >&2; return 1; }
    # Tells the app the helper is here; written only when it changes, to spare the SD card.
    if [[ "$(read_app_file "$UPDATES/helper" 64 || true)" != "version=$HELPER_VERSION" ]]; then
        printf 'version=%s\n' "$HELPER_VERSION" | write_app_file "$UPDATES/helper" || true
    fi

    recover
    case "$(take_request)" in
        update) update ;;
    esac
}

main "$@"
