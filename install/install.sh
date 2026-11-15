#!/bin/sh
# Installs Contactor on Linux, e.g. a Raspberry Pi with Raspberry Pi OS (64-bit), or Ubuntu or Debian:
#
#   curl -fsSL https://github.com/gonace/Contactor/releases/latest/download/install.sh | sh
#
# It asks nothing. It checks the machine, installs Docker if it isn't installed, puts compose.yml and the data folder
# in /opt/contactor, starts the app, puts the update helper in place (for "Update now" in Settings) and shows the
# address to open, with the setup code while the setup isn't finished.
# The board, how people reach the app and how they sign in are chosen in the setup guide. Run it as your normal user:
# it uses sudo for what needs it (Docker and /opt/contactor). Safe to run again: it repairs only what is missing and
# changes nothing that works, brings the update helper up to date, and shows the address and the setup code again.
#
#   ... | sh -s -- --uninstall                stops and removes the app and the helper; keeps /opt/contactor/data
#   ... | sh -s -- --uninstall --delete-data  removes that too
#
# The only file attached to each GitHub Release. compose.yml and the update helper (contactor-updater.sh and its
# systemd units) come from the image it installs (/app/install and /app/updater), so they always match the app: a new
# installation checks that image's signature first; run again, it takes them from the image the app runs. For
# testing only: CONTACTOR_IMAGE runs another image, and CONTACTOR_UPDATER_STATE is the helper's folder.
#
# Plain POSIX sh, so it runs with dash too. Everything is in functions, called on the last line: a download cut off
# halfway runs nothing.
set -eu

# The release this script is attached to (e.g. v2026.11.15), shown on its first line: the Release workflow fills it in.
RELEASE=""

REPOSITORY="https://github.com/gonace/Contactor"
DIR="/opt/contactor"
DATA="/opt/contactor/data"
# The image a new installation runs: the one compose.yml names when CONTACTOR_IMAGE isn't set (keep them the same).
IMAGE="ghcr.io/gonace/contactor:latest"
# Where the image keeps the files this script puts on the machine (see the Dockerfile).
IMAGE_COMPOSE="/app/install/compose.yml"
IMAGE_UPDATER="/app/updater"
# The container it makes to read them, never started: by name, so one a stopped run left behind is removed next time.
READER="contactor-install-read"
# cosign checks a new installation's image, as the update helper checks each update (contactor-updater.sh). It runs as
# a container, so nothing is installed on the machine.
COSIGN="ghcr.io/sigstore/cosign/cosign:v3.1.4@sha256:0f06154836d2de34f887a5c273506d52ca43edaa1d9e53e6510fa78a48f69c2d"
# The public keys our images are signed with, as in deploy/cosign and the update helper: the main key, and the reserve
# key whose private half is kept offline. scripts/images-sign.sh refuses to sign with a key that isn't here.
COSIGN_KEYS="-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPRbttsnxj+3I6BPt+aRzSluhz9Un
gXBxyvK5NYdVG/ywtSyKmrV6fZ90OM0CJb4aTugO0p6QU7fyh3weYfyYjg==
-----END PUBLIC KEY-----

-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/L6B3FW9vgpVpMZ9WkkUZYoriYJQ
58dJAHz8BXwrkuF99WS2Pv10Kx8L4IPTIYZbrEcIom6DAkeqWi6kZMqpMQ==
-----END PUBLIC KEY-----"
# The update helper: the only part of Contactor outside /opt/contactor (see contactor-updater.sh).
HELPER="/usr/local/sbin/contactor-updater"
UNITS="/etc/systemd/system"
HELPER_STATE="${CONTACTOR_UPDATER_STATE:-/var/lib/contactor-updater}"
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
# The image this run downloaded, until its signature is checked: removed again when the check fails or is stopped.
UNCHECKED_ID=""
# The root-only folder the files read from an image go to (see from_image).
IMAGE_TMP=""
BUSY=""
# Set when this run brought an existing update helper up to date, so it doesn't say "Nothing was changed".
HELPER_UPDATED=""

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
    if [ -n "$UNCHECKED_ID" ]; then
        docker_ image rm -f "$UNCHECKED_ID" > /dev/null 2>&1 || true
    fi
    if [ -n "$TMP" ]; then
        rm -rf -- "$TMP"
    fi
    if [ -n "$IMAGE_TMP" ]; then
        as_root rm -rf -- "$IMAGE_TMP" || true
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

# The ID of image $1 (e.g. sha256:…), or nothing when it isn't on this machine.
image_id() {
    docker_ image inspect -f '{{.Id}}' "$1" 2> /dev/null || true
}

# Copies files out of image $1 (by ID or digest) into a new root-only folder, IMAGE_TMP, each under its own name: from a
# container made from the image and never started, without a network, removed afterwards. Nothing is downloaded, and
# nothing comes from a running container (whose files the app could change). Each later argument is a path in the
# image; one that isn't there is left out, and the caller says what that means. Any other failure to copy stops it.
from_image() {
    read_image="$1"
    shift
    if [ -n "$IMAGE_TMP" ]; then
        as_root rm -rf -- "$IMAGE_TMP"
        IMAGE_TMP=""
    fi
    IMAGE_TMP="$(as_root mktemp -d)" || fail "Couldn't make a temporary folder."
    # With its anonymous volumes (the image's VOLUME /data).
    docker_ rm -f -v "$READER" > /dev/null 2>&1 || true
    if ! read_id="$(docker_ create --name "$READER" --pull never --network none "$read_image" 2> "$TMP/read.log")" \
        || [ -z "$read_id" ]; then
        docker_ rm -f -v "$READER" > /dev/null 2>&1 || true
        fail_with_log "Couldn't read Contactor's files from its image." "$TMP/read.log"
    fi
    for read_path in "$@"; do
        if ! docker_ cp "$read_id:$read_path" "$IMAGE_TMP/$(basename "$read_path")" > "$TMP/copy.log" 2>&1; then
            # Not in the image: Docker says so in one of these words. Anything else went wrong copying it.
            if ! grep -qi -e 'could not find the file' -e 'no such container:path' "$TMP/copy.log"; then
                docker_ rm -f -v "$READER" > /dev/null 2>&1 || true
                fail_with_log "Couldn't copy $read_path out of Contactor's image. Check that Docker works and the disk isn't full, then run this command again." "$TMP/copy.log"
            fi
        fi
    done
    docker_ rm -f -v "$READER" > /dev/null 2>&1 || true
}

# Whether file $1, read from an image, is there at all (also as a link or a folder).
in_image() {
    as_root test -e "$1" || as_root test -L "$1"
}

# Whether file $1, read from an image, is one of Contactor's: a regular file, not a link, not empty, with a line that
# matches $2 (an extended regular expression) and the word contactor in it.
image_file_ok() {
    as_root test -f "$1" && ! as_root test -L "$1" && as_root test -s "$1" \
        && as_root grep -Eq -e "$2" "$1" && as_root grep -q contactor "$1"
}

# Puts file $1 in place of $2 with mode $3, in one step (a new file in the same folder, then mv), so a helper that is
# running keeps reading its own copy and a cut-off copy never takes its place.
put_file() {
    as_root cp "$1" "$2.new"
    as_root chmod "$3" "$2.new"
    as_root mv -f "$2.new" "$2"
}

# Whether systemd runs this machine, which the update helper needs.
has_systemd() {
    [ -d /run/systemd/system ] && command -v systemctl > /dev/null 2>&1
}

# Whether the update helper is running right now, e.g. in the middle of an update: it holds its lock while it runs
# (a oneshot service is "activating" meanwhile, never "active", so systemctl alone doesn't tell).
updating() {
    if has_systemd; then
        case "$(as_root systemctl is-active contactor-updater.service 2> /dev/null || true)" in
            activating | deactivating | reloading) return 0 ;;
        esac
    fi
    as_root test -f "$HELPER_STATE/lock" && command -v flock > /dev/null 2>&1 \
        && ! as_root flock --nonblock "$HELPER_STATE/lock" true 2> /dev/null
}

# The repository of image $1, without its tag or digest, e.g. ghcr.io/gonace/contactor or localhost:5000/contactor.
repository_of() {
    name="${1%%@*}"
    case "${name##*/}" in
        *:*) name="${name%:*}" ;;
    esac
    printf '%s\n' "$name"
}

# The name image $1 (by ID) was pulled under by digest in the repository of image $2 (repository@sha256:…), or nothing.
digest_of() {
    digest_name="$(repository_of "$2")@sha256:"
    docker_ image inspect -f '{{range .RepoDigests}}{{println .}}{{end}}' "$1" 2> /dev/null \
        | awk -v name="$digest_name" 'index($0, name) == 1' | head -n 1 || true
}

# Whether image $1 (by digest) is signed with one of COSIGN_KEYS, as the update helper checks it (its signed()):
# 0 when it is, 1 when it isn't, 2 when it couldn't be checked (cosign couldn't be downloaded or couldn't reach the
# registry). What cosign said is in $TMP/cosign.log.
signed() {
    : > "$TMP/cosign.log"
    if [ -z "$(image_id "$COSIGN")" ] && ! docker_ pull --quiet "$COSIGN" > "$TMP/cosign.log" 2>&1; then
        return 2
    fi
    key=""
    unsigned=""
    while IFS= read -r line; do
        if [ -z "$line" ]; then
            continue
        fi
        key="${key:+$key
}$line"
        case "$line" in
            *"END PUBLIC KEY"*)
                if docker_ run --rm --network host --env COSIGN_KEY="$key" "$COSIGN" \
                    verify --key env://COSIGN_KEY --insecure-ignore-tlog=true "$1" > "$TMP/cosign.try" 2>&1; then
                    return 0
                fi
                # Only these say the image has no signature of this key; anything else is a check that didn't work.
                if grep -qi -e 'no signatures found' -e 'no matching signatures' "$TMP/cosign.try"; then
                    unsigned=1
                fi
                if [ ! -s "$TMP/cosign.log" ]; then
                    grep -v -e 'Skipping tlog verification' -e '^$' "$TMP/cosign.try" > "$TMP/cosign.log" || true
                fi
                key=""
                ;;
        esac
    done << EOF
$COSIGN_KEYS
EOF
    if [ -n "$unsigned" ]; then
        return 1
    fi
    return 2
}

# The version line of helper $1, e.g. 2; nothing when there is no such file or line.
helper_version_of() {
    { as_root sed -n 's/^readonly HELPER_VERSION=\([0-9]\{1,9\}\)$/\1/p' "$1" 2> /dev/null || true; } | head -n 1
}

# Puts the update helper of image $1 (by ID or digest) in place, or brings it up to date: the script and its two
# systemd units, from the image's /app/updater. With $2 "check" (the image the app runs, on a run again), its
# signature is checked first, by the digest it was pulled under in the repository of image $3; one that isn't signed,
# or can't be checked, leaves the helper in place. Nothing is downloaded but cosign. A file that is already the same
# stays as it is, and a helper older than the one in place never replaces it (it brings itself up to date after each
# update), though missing units are put back.
ensure_helper() {
    helper_image="$1"
    if ! has_systemd; then
        warn "No update helper: this machine doesn't run systemd, so \"Update now\" in Settings isn't available."
        return 0
    fi
    for tool in setpriv flock; do
        if ! command -v "$tool" > /dev/null 2>&1; then
            warn "No update helper: $tool isn't installed (util-linux), so \"Update now\" in Settings isn't available."
            return 0
        fi
    done
    helper_version="$(version_of "$helper_image")"
    if [ "${2:-}" = check ]; then
        running_ref="$(digest_of "$helper_image" "${3:-}")"
        result=3
        if [ -n "$running_ref" ]; then
            result=0
            signed "$running_ref" || result=$?
            if [ "$result" = 0 ]; then
                helper_image="$running_ref"
            fi
        fi
        case "$result" in
            0) ;;
            1)
                warn "Contactor${helper_version:+ $helper_version} isn't signed with Contactor's key: the update helper isn't installed or brought up to date from it."
                return 0
                ;;
            3)
                # E.g. built on this machine: only an image from a registry has a digest that is signed.
                warn "This image has no published digest, so its signature can't be checked; the update helper in place stays."
                return 0
                ;;
            *)
                warn "Couldn't check Contactor${helper_version:+ $helper_version}'s signature (no connection?): the update helper isn't installed or brought up to date."
                return 0
                ;;
        esac
    fi
    from_image "$helper_image" "$IMAGE_UPDATER/contactor-updater.sh" "$IMAGE_UPDATER/contactor-updater.service" \
        "$IMAGE_UPDATER/contactor-updater.path"
    if ! in_image "$IMAGE_TMP/contactor-updater.sh"; then
        # A release from before the image carried it. Not an error: the app runs, it only can't update itself.
        if [ -f "$HELPER" ]; then
            warn "Contactor${helper_version:+ $helper_version} brings no update helper: the one in place stays."
        else
            warn "No update helper: Contactor${helper_version:+ $helper_version} doesn't bring one, so \"Update now\" in Settings isn't available."
            more "Run the install command with --uninstall (your data is kept), then the install command again."
        fi
        return 0
    fi
    changed=""
    existed=""
    if [ -f "$HELPER" ]; then
        existed=1
    fi
    for file in contactor-updater.sh contactor-updater.service contactor-updater.path; do
        case "$file" in
            *.sh) pattern='^readonly HELPER_VERSION=[0-9]{1,9}$' ;;
            *) pattern='^\[Unit\]$' ;;
        esac
        in_image "$IMAGE_TMP/$file" || fail "Contactor's image has the update helper but not its $file ($IMAGE_UPDATER)."
        image_file_ok "$IMAGE_TMP/$file" "$pattern" || fail "$file in Contactor's image ($IMAGE_UPDATER) isn't Contactor's update helper."
    done
    # The helper is a bash script: one bash can't read would fail every update.
    if command -v bash > /dev/null 2>&1; then
        as_root bash -n "$IMAGE_TMP/contactor-updater.sh" 2> /dev/null \
            || fail "contactor-updater.sh in Contactor's image ($IMAGE_UPDATER) isn't Contactor's update helper."
    fi
    installed_version="$(helper_version_of "$HELPER")"
    image_version="$(helper_version_of "$IMAGE_TMP/contactor-updater.sh")"
    if [ -n "$installed_version" ] && [ "$image_version" -lt "$installed_version" ]; then
        warn "The update helper in place (version $installed_version) is newer than Contactor${helper_version:+ $helper_version}'s (version $image_version): it stays."
        install_units=""
    else
        install_units=1
    fi
    # Only once all three passed, so a bad file changes nothing.
    for file in contactor-updater.sh contactor-updater.service contactor-updater.path; do
        case "$file" in
            *.sh) target="$HELPER" mode=755 ;;
            *) target="$UNITS/$file" mode=644 ;;
        esac
        # A newer helper in place stays; only a unit that is missing is put back.
        if [ -z "$install_units" ] && { [ "$file" = contactor-updater.sh ] || as_root test -e "$target"; }; then
            continue
        fi
        if ! as_root cmp -s "$IMAGE_TMP/$file" "$target"; then
            put_file "$IMAGE_TMP/$file" "$target" "$mode"
            changed=1
        fi
    done
    if [ -n "$changed" ]; then
        as_root systemctl daemon-reload
    fi
    as_root systemctl enable --quiet contactor-updater.service contactor-updater.path > "$TMP/helper.log" 2>&1 \
        || fail_with_log "Couldn't set up the update helper." "$TMP/helper.log"
    as_root systemctl restart contactor-updater.path > "$TMP/helper.log" 2>&1 \
        || fail_with_log "Couldn't start the update helper." "$TMP/helper.log"
    # Once now, so the app knows it is there (it also finishes an update a power cut left halfway).
    as_root systemctl start contactor-updater.service > "$TMP/helper.log" 2>&1 \
        || fail_with_log "The update helper didn't run." "$TMP/helper.log"
    if [ -n "$changed" ] && [ -n "$existed" ]; then
        HELPER_UPDATED=1
        ok "Update helper brought up to date"
    elif [ -z "$install_units" ]; then
        ok "Update helper in place"
    else
        ok "Update helper installed"
    fi
}

# Removes the update helper, if it is here.
remove_helper() {
    found=""
    for target in "$HELPER" "$UNITS/contactor-updater.service" "$UNITS/contactor-updater.path"; do
        if [ -e "$target" ]; then
            found=1
        fi
    done
    if [ -z "$found" ]; then
        return 0
    fi
    if has_systemd; then
        as_root systemctl disable --now contactor-updater.path contactor-updater.service > /dev/null 2>&1 || true
    fi
    as_root rm -f "$HELPER" "$UNITS/contactor-updater.service" "$UNITS/contactor-updater.path"
    # The copies the helper keeps of itself and its units when it brings them up to date, and any new copy it or this
    # script (ensure_helper's .new) was writing when the power went off.
    for target in "$HELPER" "$UNITS/contactor-updater.service" "$UNITS/contactor-updater.path"; do
        for file in "$target.previous" "$target.new" "$target".new.*; do
            if [ -e "$file" ] || [ -L "$file" ]; then
                as_root rm -f -- "$file"
            fi
        done
    done
    as_root rm -rf -- "$HELPER_STATE"
    if has_systemd; then
        as_root systemctl daemon-reload > /dev/null 2>&1 || true
    fi
    # What tells the app the helper is here. As the app's user, who owns that folder.
    if [ -d "$DATA" ]; then
        as_root setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups -- rm -f "$DATA/updates/helper" 2> /dev/null || true
    fi
    ok "Update helper removed"
}

check_compose() {
    compose config --quiet > "$TMP/config.log" 2>&1 || fail_with_log "$DIR/compose.yml can't be read." "$TMP/config.log"
}

# The image to install, by digest, once its signature is checked: IMAGE_REF. The image is the one compose.yml names, or
# for a new installation CONTACTOR_IMAGE or IMAGE, downloaded unless it is here already. An image that isn't signed with
# our key stops the installation before anything changes, and one this run downloaded is removed again.
IMAGE_REF=""
ensure_image() {
    if [ -f "$DIR/compose.yml" ]; then
        check_compose
        image="$(compose config --images 2> /dev/null | head -n 1 || true)"
        [ -n "$image" ] || fail "$DIR/compose.yml names no image."
    else
        image="${CONTACTOR_IMAGE:-$IMAGE}"
    fi
    if [ -z "$(image_id "$image")" ]; then
        busy "Downloading Contactor"
        docker_ pull --quiet "$image" > "$TMP/pull.log" 2>&1 || fail_with_log "Couldn't download Contactor ($image)." "$TMP/pull.log"
        # From here on, until the check passes, it is removed again when this script stops (cleanup).
        UNCHECKED_ID="$(image_id "$image")"
        ok "Downloading Contactor $(version_of "$image")"
    fi
    IMAGE_REF="$(digest_of "$(image_id "$image")" "$image")"
    [ -n "$IMAGE_REF" ] \
        || fail "Contactor ($image) wasn't downloaded from its registry, so its signature can't be checked. Nothing was installed."
    busy "Checking Contactor's signature"
    result=0
    signed "$IMAGE_REF" || result=$?
    case "$result" in
        0) UNCHECKED_ID="" ;;
        1) fail_with_log "Contactor ($IMAGE_REF) isn't signed with Contactor's key, so it isn't installed. Nothing was changed." "$TMP/cosign.log" ;;
        *) fail_with_log "Couldn't check the signature (no connection?). Nothing was installed." "$TMP/cosign.log" ;;
    esac
    ok "Contactor $(version_of "$IMAGE_REF") is signed with Contactor's key"
}

# compose.yml and the data folder, where they are missing. An existing compose.yml stays as it is; a new one comes
# from the image it installs (IMAGE_REF), read before anything is changed.
ensure_files() {
    kept=""
    if [ -d "$DATA" ]; then
        kept=1
    fi
    new_compose=""
    if [ ! -f "$DIR/compose.yml" ]; then
        from_image "$IMAGE_REF" "$IMAGE_COMPOSE"
        if ! in_image "$IMAGE_TMP/compose.yml"; then
            old_version="$(version_of "$IMAGE_REF")"
            fail "Contactor${old_version:+ $old_version} is too old for this installer: its image has no compose.yml ($IMAGE_COMPOSE). Nothing was changed."
        fi
        image_file_ok "$IMAGE_TMP/compose.yml" '^services:' \
            || fail "compose.yml in Contactor's image ($IMAGE_COMPOSE) isn't Contactor's compose.yml. Nothing was changed."
        new_compose=1
    fi
    as_root mkdir -p "$DIR"
    as_root chmod 755 "$DIR"
    if [ -n "$new_compose" ]; then
        put_file "$IMAGE_TMP/compose.yml" "$DIR/compose.yml" 644
        check_compose
    fi
    if [ ! -d "$DATA" ]; then
        as_root mkdir -p "$DATA"
        as_root chown "$APP_UID:$APP_UID" "$DATA"
        as_root chmod 750 "$DATA"
    fi
    if [ -n "$kept" ]; then
        ok "Contactor's files in $DIR (with the data kept from before)"
    else
        ok "Contactor's files in $DIR"
    fi
}

# Starts exactly the image whose signature was checked, by its digest; never downloads.
start() {
    as_root env CONTACTOR_IMAGE="$IMAGE_REF" docker compose --project-directory "$DIR" -f "$DIR/compose.yml" \
        up --detach --no-build --pull never --remove-orphans > "$TMP/up.log" 2>&1 \
        || fail_with_log "Contactor didn't start." "$TMP/up.log"
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
        if [ -n "$again" ] && [ -z "$HELPER_UPDATED" ]; then
            box "Contactor is running." "" "Open:        $local_address" "" "Nothing was changed."
        else
            box "Contactor is running." "" "Open:        $local_address"
        fi
    elif [ -n "$again" ] && [ -z "$HELPER_UPDATED" ]; then
        box "Contactor is running and set up." "" "Open:        ${public:-$local_address}" "" "Nothing was changed."
    else
        box "Contactor is running and set up." "" "Open:        ${public:-$local_address}"
    fi
}

install() {
    if updating; then
        fail "Contactor is being updated from its Settings right now. Run this command again in a few minutes."
    fi

    # Installed and running: only the address and the setup code are shown again, once the update helper is up to date.
    if [ -f "$DIR/compose.yml" ] && [ -d "$DATA" ] && docker_works && docker_ compose version > /dev/null 2>&1; then
        cid="$(container)"
        if [ -n "$cid" ] && [ "$(docker_ inspect -f '{{.State.Running}}' "$cid" 2> /dev/null || true)" = "true" ]; then
            ok "Contactor is already installed in $DIR"
            version="$(version_of "$cid")"
            wait_ready "$cid"
            ok "Contactor${version:+ $version} is running"
            # From the image it runs, once its signature is checked.
            ensure_helper "$(docker_ inspect -f '{{.Image}}' "$cid")" check "$(docker_ inspect -f '{{.Config.Image}}' "$cid")"
            show "$cid" again
            return 0
        fi
    fi

    check_machine
    check_disk
    ensure_docker
    ensure_image
    ensure_files
    start
    cid="$(container)"
    [ -n "$cid" ] || fail "Contactor didn't start: see sudo docker logs contactor"
    wait_ready "$cid"
    ensure_helper "$IMAGE_REF"
    show "$cid" ""
}

uninstall() {
    delete_data="$1"
    if updating; then
        fail "Contactor is being updated from its Settings right now. Run this again in a few minutes. Nothing was changed."
    fi
    # Without Docker, nothing here can be running; a Docker that doesn't answer may still be running it.
    if command -v docker > /dev/null 2>&1 && ! docker_ info > /dev/null 2>&1; then
        fail "Docker doesn't answer: start it (sudo systemctl start docker) and run this again. Nothing was changed."
    fi

    # A container a stopped run of this script left behind, which would keep the image.
    if docker_works; then
        docker_ rm -f -v "$READER" > /dev/null 2>&1 || true
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
    # With the new copy a run of this script was writing when the power went off.
    as_root rm -f "$DIR/compose.yml" "$DIR/compose.yml.new"
    remove_helper

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
    printf '%sContactor installer%s%s\n\n' "$BOLD" "${RELEASE:+ $RELEASE}" "$RESET"
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
