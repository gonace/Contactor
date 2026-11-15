#!/bin/sh
# Installs Contactor on Linux, e.g. a Raspberry Pi with Raspberry Pi OS (64-bit), or Ubuntu or Debian:
#
#   curl -fsSL https://github.com/gonace/Contactor/releases/latest/download/install.sh | sh
#
# It asks nothing. It checks the machine, installs Docker if it isn't installed, puts compose.yml and the data folder
# in /opt/contactor, starts the app and shows the address to open, with the setup code while the setup isn't finished.
# The board, how people reach the app and how they sign in are chosen in the setup guide. Run it as your normal user:
# it uses sudo for what needs it (Docker and /opt/contactor). Safe to run again: it repairs only what is missing and
# changes nothing that works, and it shows the address and the setup code again.
#
#   ... | sh -s -- --uninstall                stops and removes the app; keeps /opt/contactor/data
#   ... | sh -s -- --uninstall --delete-data  removes that too
#
# Attached to each GitHub Release with that release's compose.yml, which it downloads, so the two always match.
# For testing only: CONTACTOR_IMAGE runs another image, and CONTACTOR_DOWNLOADS is where compose.yml is downloaded from.
#
# Plain POSIX sh, so it runs with dash too. Everything is in functions, called on the last line: a download cut off
# halfway runs nothing.
set -eu

# The release this script is attached to (e.g. v2026.11.15): the Images workflow fills it in. Empty: the latest.
RELEASE=""

REPOSITORY="https://github.com/gonace/Contactor"
DIR="/opt/contactor"
DATA="/opt/contactor/data"
# The image's user (the .NET images' "app" user), who must own the data folder.
APP_UID=1654
# Free disk space, in GB: below NEEDS_GB it stops, below ADVISED_GB it warns. The image is about 300 MB; the rest is
# room for updates, backups and the logs.
NEEDS_GB=2
ADVISED_GB=5
# How long it waits for the app to answer, in seconds.
WAIT_SECONDS=120
# The last release whose app has no setup-status command. Asked in a container of that release or an older one, its
# app would start a second time next to the running one; see has_setup_status.
NO_SETUP_STATUS_UNTIL="2026.11.15"

TMP=""
BUSY=""

# Colours only on a terminal that has them.
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ] && [ "${TERM:-dumb}" != "dumb" ]; then
    GREEN="$(printf '\033[32m')"
    YELLOW="$(printf '\033[33m')"
    RED="$(printf '\033[31m')"
    BOLD="$(printf '\033[1m')"
    CODE="$(printf '\033[1;33m')"
    DIM="$(printf '\033[2m')"
    RESET="$(printf '\033[0m')"
else
    GREEN="" YELLOW="" RED="" BOLD="" CODE="" DIM="" RESET=""
fi

# A step that takes a while: shown on a terminal until the next line takes its place.
busy() {
    if [ -t 1 ]; then
        printf '  %s…%s %s' "$YELLOW" "$RESET" "$1"
        BUSY=1
    fi
}

# Takes away the busy line, if there is one.
unbusy() {
    if [ -n "$BUSY" ]; then
        printf '\r\033[K'
        BUSY=""
    fi
}

ok() {
    unbusy
    printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$1"
}

note() {
    unbusy
    printf '  %s…%s %s\n' "$YELLOW" "$RESET" "$1"
}

warn() {
    unbusy
    printf '  %s!%s %s\n' "$YELLOW" "$RESET" "$1"
}

# A line under the one before it.
more() {
    printf '    %s\n' "$1"
}

fail() {
    unbusy
    printf '  %s✗%s %s\n' "$RED" "$RESET" "$1" >&2
    exit 1
}

# Fails with the last lines of a log of what went wrong.
fail_with_log() {
    unbusy
    printf '  %s✗%s %s\n' "$RED" "$RESET" "$1" >&2
    if [ -s "$2" ]; then
        printf '    %sIt said:%s\n' "$DIM" "$RESET" >&2
        tail -n 15 "$2" | sed 's/^/      /' >&2
    fi
    exit 1
}

cleanup() {
    if [ -n "$TMP" ]; then
        rm -rf -- "$TMP"
    fi
}

usage() {
    cat <<EOF
Installs Contactor, or runs it again to see its address and setup code:
  curl -fsSL $REPOSITORY/releases/latest/download/install.sh | sh
Removes it, keeping its data in $DATA:
  curl -fsSL $REPOSITORY/releases/latest/download/install.sh | sh -s -- --uninstall
Removes its data too:
  curl -fsSL $REPOSITORY/releases/latest/download/install.sh | sh -s -- --uninstall --delete-data
EOF
}

# Runs a command as root: with sudo, unless this is root already.
as_root() {
    if [ "$(id -u)" = 0 ]; then
        "$@"
    else
        sudo "$@"
    fi
}

docker_() {
    as_root env ${CONTACTOR_IMAGE:+"CONTACTOR_IMAGE=$CONTACTOR_IMAGE"} docker "$@"
}

compose() {
    docker_ compose --project-directory "$DIR" -f "$DIR/compose.yml" "$@"
}

# Asks for the sudo password once, at the start, rather than in the middle of a step.
need_root() {
    if [ "$(id -u)" = 0 ]; then
        return 0
    fi
    command -v sudo > /dev/null 2>&1 || fail "This needs sudo, which isn't installed. Run it as root, or install sudo first."
    if ! sudo -n true 2> /dev/null; then
        printf '  Some steps need sudo (Docker and %s): it may ask for your password.\n' "$DIR"
        # sudo asks on the terminal itself, also when this script comes in on its standard input.
        sudo -v || fail "sudo didn't let this user in. Run it as a user who may use sudo."
    fi
}

# Whether Docker is installed and its service answers.
docker_works() {
    command -v docker > /dev/null 2>&1 && docker_ info > /dev/null 2>&1
}

# The app's container, if there is one.
container() {
    compose ps --all --quiet contactor 2> /dev/null | head -n 1 || true
}

# The release an image or container is, from the label the Dockerfile sets, e.g. 2026.11.15.
version_of() {
    docker_ inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2> /dev/null || true
}

check_machine() {
    [ "$(uname -s)" = "Linux" ] || fail "This installer is for Linux, e.g. Raspberry Pi OS or Ubuntu; this is $(uname -s)."
    machine="$(uname -m)"
    case "$machine" in
        aarch64 | arm64) arch=arm64 ;;
        x86_64 | amd64) arch=amd64 ;;
        armv6* | armv7* | armhf) fail "Contactor needs a 64-bit system. On a Raspberry Pi: Raspberry Pi OS (64-bit)." ;;
        *) fail "Contactor runs on arm64 and amd64 machines; this one is $machine." ;;
    esac
    # A 64-bit kernel can run a 32-bit system, e.g. an older Raspberry Pi OS.
    if command -v dpkg > /dev/null 2>&1 && [ "$(dpkg --print-architecture 2> /dev/null || true)" = "armhf" ]; then
        fail "Contactor needs a 64-bit system, and this one is 32-bit. On a Raspberry Pi: Raspberry Pi OS (64-bit)."
    fi
    model=""
    for file in /proc/device-tree/model /sys/firmware/devicetree/base/model; do
        if [ -r "$file" ]; then
            model="$(tr -d '\000' < "$file" | sed -n 's/^\(Raspberry Pi.*\)$/\1/p' | sed 's/ Model .*//; s/ Rev .*//')"
            break
        fi
    done
    ok "Linux, $arch${model:+ ($model)}"
}

# Free space in GB, rounded down, on the fuller of the folders Docker's images and Contactor's data go to.
free_gb() {
    least=""
    for folder in /var/lib /opt; do
        while [ ! -d "$folder" ]; do
            folder="$(dirname "$folder")"
        done
        kb="$(df -Pk "$folder" 2> /dev/null | awk 'NR == 2 { print $4 }')"
        case "$kb" in
            '' | *[!0-9]*) continue ;;
        esac
        if [ -z "$least" ] || [ "$kb" -lt "$least" ]; then
            least="$kb"
        fi
    done
    [ -n "$least" ] || fail "Couldn't find out how much disk space is free."
    echo $((least / 1024 / 1024))
}

check_disk() {
    gb="$(free_gb)"
    if [ "$gb" -lt "$NEEDS_GB" ]; then
        fail "Only $gb GB free disk space: Contactor needs $NEEDS_GB GB. Make room and run this command again."
    elif [ "$gb" -lt "$ADVISED_GB" ]; then
        warn "$gb GB free disk space (needs $NEEDS_GB GB; $ADVISED_GB GB or more leaves room for updates and backups)"
    else
        ok "$gb GB free disk space (needs $NEEDS_GB GB)"
    fi
}

download() {
    url="$1"
    target="$2"
    if command -v curl > /dev/null 2>&1; then
        curl -fsSL --retry 3 -o "$target" "$url"
    elif command -v wget > /dev/null 2>&1; then
        wget -q -O "$target" "$url"
    else
        fail "Neither curl nor wget is installed: install curl and run this command again."
    fi
}

install_docker() {
    note "Installing Docker (it wasn't installed)"
    download https://get.docker.com "$TMP/get-docker.sh" || fail "Couldn't download Docker's installer from get.docker.com."
    as_root sh "$TMP/get-docker.sh" < /dev/null > "$TMP/docker.log" 2>&1 || fail_with_log "Docker's installer failed." "$TMP/docker.log"
}

# Docker's Compose plugin, for a Docker that came without it (e.g. from the distribution's own packages):
# docker-compose-plugin from Docker's packages, or docker-compose-v2 from Ubuntu's. Never the docker-compose package:
# in Debian 12 and older that is Compose v1, which doesn't add "docker compose".
install_compose() {
    command -v apt-get > /dev/null 2>&1 || fail "Docker Compose isn't installed: install Docker's compose plugin and run this command again."
    busy "Installing Docker Compose"
    # A list that can't be updated (e.g. one broken source) may still have the package: what install says counts.
    as_root apt-get update < /dev/null > "$TMP/compose.log" 2>&1 || true
    for package in docker-compose-plugin docker-compose-v2; do
        if as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y "$package" < /dev/null >> "$TMP/compose.log" 2>&1 \
            && docker_ compose version > /dev/null 2>&1; then
            return 0
        fi
    done
    fail_with_log "Couldn't install Docker Compose: install Docker's compose plugin and run this command again." "$TMP/compose.log"
}

ensure_docker() {
    if ! command -v docker > /dev/null 2>&1; then
        install_docker
    fi
    if ! docker_ info > /dev/null 2>&1 && command -v systemctl > /dev/null 2>&1; then
        as_root systemctl enable --now docker > /dev/null 2>&1 || true
    fi
    docker_ info > /dev/null 2>&1 || fail "Docker is installed but doesn't answer. Start it (sudo systemctl start docker) and run this command again."
    if ! docker_ compose version > /dev/null 2>&1; then
        install_compose
    fi
    ok "Docker $(docker_ version -f '{{.Server.Version}}' 2> /dev/null || echo "")"
}

# compose.yml and the data folder, where they are missing. An existing compose.yml stays as it is.
ensure_files() {
    kept=""
    if [ -d "$DATA" ]; then
        kept=1
    fi
    as_root mkdir -p "$DIR"
    as_root chmod 755 "$DIR"
    if [ ! -f "$DIR/compose.yml" ]; then
        if [ -n "${CONTACTOR_DOWNLOADS:-}" ]; then
            from="$CONTACTOR_DOWNLOADS"
        elif [ -n "$RELEASE" ]; then
            from="$REPOSITORY/releases/download/$RELEASE"
        else
            from="$REPOSITORY/releases/latest/download"
        fi
        download "$from/compose.yml" "$TMP/compose.yml" || fail "Couldn't download compose.yml from $from."
        grep -q 'contactor' "$TMP/compose.yml" || fail "What came from $from/compose.yml isn't Contactor's compose.yml."
        as_root cp "$TMP/compose.yml" "$DIR/compose.yml"
        as_root chmod 644 "$DIR/compose.yml"
    fi
    if [ ! -d "$DATA" ]; then
        as_root mkdir -p "$DATA"
        as_root chown "$APP_UID:$APP_UID" "$DATA"
        as_root chmod 750 "$DATA"
    fi
    compose config --quiet > "$TMP/config.log" 2>&1 || fail_with_log "$DIR/compose.yml can't be read." "$TMP/config.log"
    if [ -n "$kept" ]; then
        ok "Contactor's files in $DIR (with the data kept from before)"
    else
        ok "Contactor's files in $DIR"
    fi
}

# Downloads the image, unless it is here already: what works is kept as it is.
ensure_image() {
    image="$(compose config --images 2> /dev/null | head -n 1 || true)"
    [ -n "$image" ] || fail "$DIR/compose.yml names no image."
    if docker_ image inspect "$image" > /dev/null 2>&1; then
        return 0
    fi
    busy "Downloading Contactor"
    compose pull --quiet > "$TMP/pull.log" 2>&1 || fail_with_log "Couldn't download Contactor ($image)." "$TMP/pull.log"
    ok "Downloading Contactor $(version_of "$image")"
}

start() {
    compose up --detach --remove-orphans > "$TMP/up.log" 2>&1 || fail_with_log "Contactor didn't start." "$TMP/up.log"
    ok "Contactor started"
}

# Fails, showing the last lines of the app's log and how to see all of it.
fail_with_app_log() {
    unbusy
    printf '  %s✗%s %s\n' "$RED" "$RESET" "$1" >&2
    docker_ logs --tail 20 "$2" > "$TMP/app.log" 2>&1 || true
    if [ -s "$TMP/app.log" ]; then
        printf '    %sThe last lines of its log:%s\n' "$DIM" "$RESET" >&2
        sed 's/^/      /' "$TMP/app.log" >&2
    fi
    printf '    To see more: sudo docker logs contactor\n' >&2
    exit 1
}

# The container's state, restart count and health, e.g. "running 0 healthy" (no health: "running 0 ").
state_of() {
    docker_ inspect -f '{{.State.Status}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2> /dev/null || true
}

# Waits until the app answers its health check. Fails with the app's log when it stops or its health check fails,
# and, saying to run it again, when it is still starting after WAIT_SECONDS.
wait_ready() {
    cid="$1"
    waited=0
    restarts="$(state_of "$cid" | awk '{ print $2 }')"
    busy "Waiting for Contactor to be ready (up to 2 minutes)"
    while [ "$waited" -lt "$WAIT_SECONDS" ]; do
        # shellcheck disable=SC2046 # three words, split on purpose
        set -- $(state_of "$cid")
        if [ "${3:-}" = "healthy" ]; then
            unbusy
            return 0
        fi
        # It stopped: Docker restarted it since this began, is about to, or it stays stopped. (A stopped container's
        # health check says unhealthy too, so this comes first.)
        case "${1:-}" in
            exited | dead | restarting) fail_with_app_log "Contactor didn't start: it stopped with an error." "$cid" ;;
        esac
        if [ -n "${2:-}" ] && [ -n "$restarts" ] && [ "$2" -gt "$restarts" ]; then
            fail_with_app_log "Contactor didn't start: it stopped with an error." "$cid"
        fi
        if [ "${3:-}" = "unhealthy" ]; then
            fail_with_app_log "Contactor is running, but it doesn't answer: its health check fails." "$cid"
        fi
        sleep 2
        waited=$((waited + 2))
    done
    if [ -z "$BUSY" ]; then
        note "Waiting for Contactor to be ready (up to 2 minutes)"
    else
        printf '\n'
        BUSY=""
    fi
    warn "Contactor is taking longer than usual to start."
    more "It may still be starting: run this command again in a minute"
    more "to see the address and the setup code."
    exit 1
}

# This machine's address on the local network.
lan_address() {
    address="$(ip -4 route get 1.1.1.1 2> /dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1 || true)"
    if [ -z "$address" ]; then
        address="$(hostname -I 2> /dev/null | awk '{ print $1 }' || true)"
    fi
    if [ -z "$address" ]; then
        address="$(hostname)"
    fi
    echo "$address"
}

# The setup code the app wrote to its log since it last started, e.g. KRTW-48PX, or nothing. Only from the setup
# code's own log line, as the console log writes it: the line with its category (SetupCode), then the message.
setup_code() {
    cid="$1"
    since="$(docker_ inspect -f '{{.State.StartedAt}}' "$cid" 2> /dev/null || true)"
    docker_ logs ${since:+--since "$since"} "$cid" 2>&1 | awk '
        category && /^      Contactor is being set up\. The setup code is [A-Z0-9][A-Z0-9][A-Z0-9][A-Z0-9]-[A-Z0-9][A-Z0-9][A-Z0-9][A-Z0-9]: type it in the setup guide$/ {
            code = $0
            sub(/^      Contactor is being set up\. The setup code is /, "", code)
            sub(/:.*$/, "", code)
        }
        { category = /^warn: Contactor\.Web\.Features\.Setup\.SetupCode\[[0-9]*\]$/ }
        END { print code }' || true
}

# Whether the app in the container has the setup-status command, from its version (the image label): a release
# after NO_SETUP_STATUS_UNTIL, or a development build. An older app doesn't know the command and starts as usual, a
# second time in the running container, so it is never asked.
has_setup_status() {
    release="$1"
    case "$release" in
        0.0.0-dev) return 0 ;;
        '' | *[!0-9.]* | .* | *. | *..*) return 1 ;;
    esac
    awk -v a="$release" -v b="$NO_SETUP_STATUS_UNTIL" 'BEGIN {
        n = split(a, x, "."); m = split(b, y, "."); k = n > m ? n : m
        for (i = 1; i <= k; i++) {
            p = i <= n ? x[i] + 0 : 0; q = i <= m ? y[i] + 0 : 0
            if (p != q) { exit p > q ? 0 : 1 }
        }
        exit 1
    }'
}

# The box: its rows are lines of text; "Open:" and "Setup code:" rows get their value highlighted.
box() {
    width=50
    for row in "$@"; do
        if [ $((${#row} + 6)) -gt "$width" ]; then
            width=$((${#row} + 6))
        fi
    done
    line=""
    i=0
    while [ "$i" -lt "$width" ]; do
        line="$line─"
        i=$((i + 1))
    done
    printf '  ┌%s┐\n' "$line"
    printf "  │%${width}s│\n" ""
    for row in "$@"; do
        shown="$row"
        case "$row" in
            "Contactor is running"*) shown="$BOLD$row$RESET" ;;
            "Open:        "*) shown="Open:        $BOLD${row#Open:        }$RESET" ;;
            "Setup code:  "*) shown="Setup code:  $CODE${row#Setup code:  }$RESET" ;;
        esac
        printf "  │   %s%$((width - 3 - ${#row}))s│\n" "$shown" ""
    done
    printf "  │%${width}s│\n" ""
    printf '  └%s┘\n' "$line"
}

# What the app says about its setup, and the box with the address and, while it isn't set up, the setup code.
# $2: "again" when nothing was changed.
show() {
    cid="$1"
    again="$2"
    status=""
    if has_setup_status "$(version_of "$cid")"; then
        status="$(docker_ exec "$cid" dotnet /app/Contactor.Web.dll setup-status 2> /dev/null || true)"
    fi
    state="$(printf '%s\n' "$status" | sed -n 1p)"
    # Only a plain address, so nothing else from the database reaches the terminal.
    public="$(printf '%s\n' "$status" | sed -n 2p | grep -E '^https://[A-Za-z0-9.-]+$' || true)"
    local_address="http://$(lan_address):8080"
    code=""
    case "$state" in
        done | setup-guide) ;;
        *) code="$(setup_code "$cid")" ;;
    esac

    echo
    if [ -n "$code" ] || [ "$state" = "setup-code" ]; then
        if [ -n "$code" ]; then
            code_row="Setup code:  $code"
        else
            code_row="Setup code:  in the app's log: sudo docker logs contactor"
        fi
        if [ -n "$again" ]; then
            box "Contactor is running." "" "Open:        $local_address" "$code_row" "" \
                "The setup isn't finished yet: type the code" "on that page to go on with it."
        else
            box "Contactor is running." "" "Open:        $local_address" "$code_row" "" \
                "Type the code on that page to start the" "setup guide."
        fi
    elif [ "$state" = "setup-guide" ]; then
        box "Contactor is running." "" "Open:        ${public:-$local_address}" "" \
            "The setup isn't finished yet: open that page" "to go on with it."
    elif [ "$state" != "done" ]; then
        # The app didn't say how far the setup is (e.g. an older image): only what is sure.
        if [ -n "$again" ]; then
            box "Contactor is running." "" "Open:        $local_address" "" "Nothing was changed."
        else
            box "Contactor is running." "" "Open:        $local_address"
        fi
    elif [ -n "$again" ]; then
        box "Contactor is running and set up." "" "Open:        ${public:-$local_address}" "" "Nothing was changed."
    else
        box "Contactor is running and set up." "" "Open:        ${public:-$local_address}"
    fi
}

install() {
    # One made by the earlier installer (deploy/install.sh), with its settings in .env and its data elsewhere. Its
    # folder is root's only, so it is looked at as root.
    if as_root test -f "$DIR/.env"; then
        fail "Contactor was installed here with the earlier installer ($DIR/.env). This one can't move it over yet: nothing was changed."
    fi

    # Installed and running: only the address and the setup code are shown again.
    if [ -f "$DIR/compose.yml" ] && [ -d "$DATA" ] && docker_works && docker_ compose version > /dev/null 2>&1; then
        cid="$(container)"
        if [ -n "$cid" ] && [ "$(docker_ inspect -f '{{.State.Running}}' "$cid" 2> /dev/null || true)" = "true" ]; then
            ok "Contactor is already installed in $DIR"
            version="$(version_of "$cid")"
            wait_ready "$cid"
            ok "Contactor${version:+ $version} is running"
            show "$cid" again
            return 0
        fi
    fi

    check_machine
    check_disk
    ensure_docker
    ensure_files
    ensure_image
    start
    cid="$(container)"
    [ -n "$cid" ] || fail "Contactor didn't start: see sudo docker logs contactor"
    wait_ready "$cid"
    show "$cid" ""
}

uninstall() {
    delete_data="$1"
    if as_root test -f "$DIR/.env"; then
        fail "Contactor was installed here with the earlier installer ($DIR/.env). This one can't remove it: nothing was changed."
    fi
    # Without Docker, nothing here can be running; a Docker that doesn't answer may still be running it.
    if command -v docker > /dev/null 2>&1 && ! docker_ info > /dev/null 2>&1; then
        fail "Docker doesn't answer: start it (sudo systemctl start docker) and run this again. Nothing was changed."
    fi

    if [ -f "$DIR/compose.yml" ] && docker_works; then
        compose down --remove-orphans --rmi all > "$TMP/down.log" 2>&1 || fail_with_log "Couldn't stop Contactor." "$TMP/down.log"
        ok "Contactor stopped and removed"
    elif docker_works && [ -n "$(docker_ ps --all --quiet --filter label=com.docker.compose.project=contactor)" ]; then
        # compose.yml is gone, but its container is still here.
        docker_ compose --project-name contactor down --remove-orphans --rmi all > "$TMP/down.log" 2>&1 \
            || fail_with_log "Couldn't stop Contactor." "$TMP/down.log"
        ok "Contactor stopped and removed"
    else
        ok "Contactor isn't running here"
    fi
    as_root rm -f "$DIR/compose.yml"

    if [ -n "$delete_data" ]; then
        if [ -e "$DATA" ] || [ -L "$DATA" ]; then
            as_root rm -rf -- "$DATA"
            ok "Your data in $DATA is deleted"
        else
            ok "There was no data in $DATA"
        fi
        # Only when nothing else is in it.
        if [ -d "$DIR" ]; then
            as_root rmdir "$DIR" 2> /dev/null || true
        fi
    elif [ -d "$DATA" ]; then
        ok "Your data is kept in $DATA"
        more "(the database, the backups and the event log)"
        printf '  To delete that too, run it with --uninstall --delete-data.\n'
        printf '  Installing again later picks up where you left off.\n'
    fi
}

main() {
    action=install
    delete_data=""
    for argument in "$@"; do
        case "$argument" in
            --uninstall) action=uninstall ;;
            --delete-data) delete_data=1 ;;
            -h | --help)
                usage
                exit 0
                ;;
            *)
                usage >&2
                exit 2
                ;;
        esac
    done
    printf '%sContactor installer%s\n\n' "$BOLD" "$RESET"
    if [ -n "$delete_data" ] && [ "$action" != "uninstall" ]; then
        fail "--delete-data only goes with --uninstall."
    fi
    [ "$(uname -s)" = "Linux" ] || fail "This installer is for Linux, e.g. Raspberry Pi OS or Ubuntu; this is $(uname -s)."
    need_root
    TMP="$(mktemp -d)"
    trap cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM

    if [ "$action" = "uninstall" ]; then
        uninstall "$delete_data"
    else
        install
    fi
}

main "$@"
